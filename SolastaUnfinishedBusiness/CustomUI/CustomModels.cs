using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;

namespace SolastaUnfinishedBusiness.CustomUI;

public static class CustomModels
{
    private static readonly HashSet<WieldedModelPresentation> WieldedModels = [];

    internal static bool RequiresWieldedEquipmentRefresh(ItemDefinition definition) =>
        definition.IsWeapon ||
        GetWieldedItemFeature<WieldedModel>(definition) != null ||
        GetWieldedItemFeature<WieldedGrip>(definition) != null ||
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

    // Coordinates belong to the authored model. Attaching a model does not change
    // native weapon bones, animation events or deterministic projectile origins.
    internal sealed class WieldedModel(string[] parts, Vector3 gripPoint, Vector3 gripAxis,
        Vector3 railAxis, Vector3 upAxis, Vector3 muzzlePoint)
    {
        internal string[] Parts { get; } = parts;
        internal Vector3 GripPoint { get; } = gripPoint;
        internal Vector3 GripAxis { get; } = gripAxis;
        internal Vector3 RailAxis { get; } = railAxis;
        internal Vector3 UpAxis { get; } = upAxis;
        internal Vector3 MuzzlePoint { get; } = muzzlePoint;
        internal WieldedMechanism Mechanism { get; private set; }

        internal WieldedModel WithMechanism(WieldedMechanism mechanism)
        {
            if (mechanism == null)
            {
                throw new ArgumentNullException(nameof(mechanism));
            }
            return new WieldedModel((string[])Parts.Clone(), GripPoint, GripAxis, RailAxis, UpAxis,
                MuzzlePoint) { Mechanism = mechanism };
        }
    }

    // Immutable authored geometry/driver data. Native animation remains the clock;
    // the item presentation below owns all mutable meshes and native references.
    internal sealed class WieldedMechanism
    {
        internal readonly struct Point(double x, double y, double z)
        {
            internal readonly double X = x, Y = y, Z = z;
            internal double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
            internal Point Unit => this * (1 / Length);
            internal Vector3 Float => new((float)X, (float)Y, (float)Z);
            internal static Point From(Vector3 p) => new(p.x, p.y, p.z);
            public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static Point operator *(Point a, double k) => new(a.X * k, a.Y * k, a.Z * k);
            internal double Dot(Point b) => X * b.X + Y * b.Y + Z * b.Z;
            internal Point Cross(Point b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        }

        internal sealed class Joint(Point pivot, Point axis, double maximum, double travel)
        {
            internal readonly Point Pivot = pivot, Axis = axis;
            internal readonly double Maximum = maximum, Travel = travel;
            internal double Angle(double q)
            {
                var t = Math.Max(0, Math.Min(1, q / Travel));
                return Maximum * t * t * (3 - 2 * t);
            }
            internal Point Rotate(Point p, double angle)
            {
                var c = Math.Cos(angle);
                var s = Math.Sin(angle);
                return p * c + Axis.Cross(p) * s + Axis * (Axis.Dot(p) * (1 - c));
            }
        }

        internal readonly string FramePart, StringPart, FrameHash, StringHash, BoltHash;
        internal readonly string NativeGuid, ControllerName, BoltPath;
        internal readonly IReadOnlyList<string> BonePaths, ClipNames, BoltParts;
        internal readonly IReadOnlyList<(double X, double Y, double Z, double W)> LeftObserver, RightObserver;
        internal readonly Point RopeLoaded, RopeAxis, Root, NockLoaded, NockTravel, BoltMinimum, BoltMaximum;
        internal readonly double NativeSpanLoaded, NativeSpanMaximum, SpanBase, SpanRange, MaterialLength,
            CordLength, TipY, TipOffset, WidthRoot, WidthTip, Residual, BoltQMinimum, BoltQMaximum;
        internal readonly int Segments, Iterations, FrameCount;
        internal readonly IReadOnlyList<(double Height, double Sign)> Corners;
        internal readonly IReadOnlyList<(int Index, bool Left, int Section, int Corner)> LimbRows;
        internal readonly IReadOnlyList<(int Index, Point Position, Joint Joint)> RigidRows;
        internal readonly IReadOnlyList<(int Ring, Point Offset)> StringRows;
        internal readonly IReadOnlyList<int> StringIndices;
        internal readonly IReadOnlyList<(double Q, double Span, double A, double B)> Seeds;

        internal static WieldedMechanism FromJson(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            using var text = new StreamReader(stream, System.Text.Encoding.UTF8, true, 4096, true);
            using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None };
            try
            {
                return new WieldedMechanism(JObject.Load(reader));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or
                                           OverflowException or InvalidCastException or KeyNotFoundException)
            {
                throw new InvalidDataException("Invalid authored equipment mechanism.", ex);
            }
        }

        private static JToken Required(JToken value, string key) => value?[key] ??
            throw new InvalidDataException("Missing mechanism field: " + key);
        private static double Number(JToken value)
        {
            if (value == null || value.Type is not (JTokenType.Float or JTokenType.Integer))
                throw new InvalidDataException("Mechanism number required.");
            var result = value.Value<double>();
            if (double.IsNaN(result) || double.IsInfinity(result))
                throw new InvalidDataException("Non-finite mechanism number.");
            return result;
        }
        private static int Integer(JToken value)
        {
            var n = Number(value);
            if (n < 0 || n > int.MaxValue || n != Math.Floor(n))
                throw new InvalidDataException("Invalid mechanism index.");
            return (int)n;
        }
        private static string Text(JToken value)
        {
            if (value?.Type != JTokenType.String || string.IsNullOrEmpty(value.Value<string>()))
                throw new InvalidDataException("Mechanism name required.");
            return value.Value<string>();
        }
        private static JArray Array(JToken value, int count = -1)
        {
            if (value is not JArray array || count >= 0 && array.Count != count)
                throw new InvalidDataException("Invalid mechanism array.");
            return array;
        }
        private static Point ReadPoint(JToken value)
        {
            var p = Array(value, 3);
            return new Point(Number(p[0]), Number(p[1]), Number(p[2]));
        }
        private static IReadOnlyList<string> Names(JToken value) =>
            System.Array.AsReadOnly(Array(value).Select(Text).ToArray());
        private static string Hash(JToken value)
        {
            var hash = Text(value);
            if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Invalid mechanism source hash.");
            return hash;
        }
        private static IReadOnlyList<(double X, double Y, double Z, double W)> Observer(JToken value)
        {
            var rows = Array(Required(value, "homogeneousCoefficientsByBone"), 6);
            return System.Array.AsReadOnly(rows.Select(row =>
            {
                var p = Array(row, 4);
                return (Number(p[0]), Number(p[1]), Number(p[2]), Number(p[3]));
            }).ToArray());
        }

        private WieldedMechanism(JObject data)
        {
            if (Integer(Required(data, "schemaVersion")) != 3)
                throw new InvalidDataException("Unsupported mechanism schema.");
            var parts = Required(data, "parts");
            FramePart = Text(Required(parts, "frame"));
            StringPart = Text(Required(parts, "string"));
            BoltParts = Names(Required(parts, "bolts"));
            if (new[] { FramePart, StringPart }.Concat(BoltParts).Distinct().Count() !=
                BoltParts.Count + 2 || new[] { FramePart, StringPart }.Concat(BoltParts).Any(name =>
                    Path.GetFileName(name) != name || name is "." or ".."))
                throw new InvalidDataException("Invalid mechanism part path.");
            FrameHash = Hash(Required(data, "frameObjSha256"));
            StringHash = Hash(Required(data, "stringObjSha256"));
            var native = Required(data, "nativeSource");
            NativeGuid = Text(Required(native, "assetGuid"));
            ControllerName = Text(Required(native, "controllerName"));
            BonePaths = Names(Required(native, "bonePaths"));
            ClipNames = Names(Required(native, "sourceClipNames"));
            if (BonePaths.Count != 6 || BonePaths.Distinct().Count() != 6 || ClipNames.Count == 0)
                throw new InvalidDataException("Invalid native mechanism rig.");
            if (!BonePaths[0].EndsWith("/" + Text(Required(native, "rootBoneName")), StringComparison.Ordinal) ||
                BonePaths.Skip(1).Any(path => !path.StartsWith(BonePaths[0] + "/", StringComparison.Ordinal)))
                throw new InvalidDataException("Invalid native mechanism hierarchy.");
            var drivers = Required(data, "drivers");
            var rope = Required(drivers, "ropeProjection");
            RopeLoaded = ReadPoint(Required(rope, "loadedPosition"));
            RopeAxis = ReadPoint(Required(rope, "projectionAxis"));
            if (RopeAxis.Length == 0 || BonePaths[1] != BonePaths[0] + "/" + Text(Required(rope, "bone")))
                throw new InvalidDataException("Invalid rope driver.");
            var observers = Required(drivers, "sectionObservers");
            var left = Required(observers, "leftStringTipF");
            var right = Required(observers, "rightStringTipF");
            if (!Names(Required(left, "bonePaths")).SequenceEqual(BonePaths) ||
                !Names(Required(right, "bonePaths")).SequenceEqual(BonePaths))
                throw new InvalidDataException("Mismatched mechanism observer rig.");
            LeftObserver = Observer(left);
            RightObserver = Observer(right);
            NativeSpanLoaded = Number(Required(drivers, "nativeLoadedTipSpan"));
            NativeSpanMaximum = Number(Required(drivers, "nativeMaximumTipSpan"));
            SpanBase = Number(Required(drivers, "modelTipSpanBase"));
            SpanRange = Number(Required(drivers, "modelTipSpanRange"));
            NockLoaded = ReadPoint(Required(drivers, "nockLoaded"));
            NockTravel = ReadPoint(Required(drivers, "nockTravel"));
            var limb = Required(data, "limb");
            MaterialLength = Number(Required(limb, "materialLength"));
            CordLength = Number(Required(limb, "cordLength"));
            Segments = Integer(Required(limb, "segments"));
            Root = ReadPoint(Required(limb, "rightRoot"));
            var leftRoot = ReadPoint(Required(limb, "leftRoot"));
            TipY = Number(Required(limb, "tipY"));
            TipOffset = Number(Required(limb, "tipRearSurfaceOffset"));
            WidthRoot = Number(Required(limb, "sectionHalfWidthRoot"));
            WidthTip = Number(Required(limb, "sectionHalfWidthTip"));
            var solver = Required(limb, "solver");
            Iterations = Integer(Required(solver, "maximumIterations"));
            Residual = Number(Required(solver, "residualTolerance"));
            if (Segments < 2 || Iterations < 1 || Iterations > 60 || Residual <= 0 ||
                MaterialLength <= Math.Abs(TipY - Root.Y) || CordLength <= 0 ||
                NativeSpanMaximum <= NativeSpanLoaded || WidthRoot <= 0 || WidthTip <= 0 ||
                leftRoot.X != -Root.X || leftRoot.Y != Root.Y || leftRoot.Z != Root.Z)
                throw new InvalidDataException("Invalid mechanism dimensions.");
            Corners = System.Array.AsReadOnly(Array(Required(limb, "cornerHeightAndNormalSign"), 4)
                .Select(row => { var a = Array(row, 2); return (Number(a[0]), Number(a[1])); }).ToArray());
            var mapping = Required(limb, "frameExpandedVertexContract");
            if (!string.Equals(Hash(Required(mapping, "objSha256")), FrameHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Mismatched mechanism Frame hash.");
            FrameCount = Integer(Required(mapping, "expandedVertexCount"));
            var occupied = new HashSet<int>();
            LimbRows = System.Array.AsReadOnly(Array(Required(mapping, "limbVertices")).Select(row =>
            {
                var index = Integer(Required(row, "expandedVertexIndex"));
                var section = Integer(Required(row, "section"));
                var corner = Integer(Required(row, "corner"));
                var side = Text(Required(row, "side"));
                if (index >= FrameCount || section > Segments || corner >= 4 || !occupied.Add(index) ||
                    side is not ("Left" or "Right")) throw new InvalidDataException("Invalid limb vertex map.");
                return (index, side == "Left", section, corner);
            }).ToArray());
            Seeds = System.Array.AsReadOnly(Array(Required(limb, "verifiedNativeStateSeeds")).Select(row =>
            {
                var a = Array(Required(row, "solverAngles"), 2);
                return (Number(Required(row, "release")), Number(Required(row, "nativeSourceTipSpan")),
                    Number(a[0]), Number(a[1]));
            }).ToArray());
            if (Seeds.Count == 0) throw new InvalidDataException("Missing mechanism solver seeds.");
            var locking = Required(data, "lock");
            var laws = Required(locking, "jointLaw");
            var joints = new Dictionary<string, Joint>(StringComparer.Ordinal);
            foreach (var property in ((JObject)Required(locking, "rigidGroupJoints")).Properties())
            {
                var key = Text(property.Value);
                if (joints.ContainsKey(key)) continue;
                var law = Required(laws, key);
                var axis = ReadPoint(Required(law, "axis"));
                var travel = Number(Required(law, "fullTravelQ"));
                if (Math.Abs(axis.Length - 1) > 0.000001 || travel <= 0)
                    throw new InvalidDataException("Invalid mechanism joint.");
                joints[key] = new Joint(ReadPoint(Required(law, "pivot")), axis,
                    Number(Required(law, "maximumRadians")), travel);
            }
            var rigid = new List<(int, Point, Joint)>();
            foreach (var group in ((JObject)Required(locking, "rigidExpandedVertices")).Properties())
            {
                var joint = joints[Text(Required(Required(locking, "rigidGroupJoints"), group.Name))];
                foreach (var row in Array(group.Value))
                {
                    var index = Integer(Required(row, "expandedVertexIndex"));
                    if (index >= FrameCount || !occupied.Add(index))
                        throw new InvalidDataException("Invalid rigid vertex map.");
                    rigid.Add((index, ReadPoint(Required(row, "loadedModelPosition")), joint));
                }
            }
            RigidRows = rigid.AsReadOnly();
            var cord = Required(data, "string");
            if (!string.Equals(Hash(Required(cord, "objSha256")), StringHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Mismatched mechanism String hash.");
            StringRows = System.Array.AsReadOnly(Array(Required(cord, "uniqueVertices")).Select(row =>
            {
                var ring = Integer(Required(row, "ring"));
                if (ring > 2) throw new InvalidDataException("Invalid string ring.");
                return (ring, ReadPoint(Required(row, "offsetInTangentUpSide")));
            }).ToArray());
            StringIndices = System.Array.AsReadOnly(Array(Required(cord, "expandedSourceVertexIndices"))
                .Select(row => { var i = Integer(row); if (i >= StringRows.Count)
                    throw new InvalidDataException("Invalid string vertex map."); return i; }).ToArray());
            var bolt = Required(data, "bolt");
            BoltPath = Text(Required(bolt, "nativeBonePath"));
            BoltMinimum = ReadPoint(Required(bolt, "loadedPositionMin"));
            BoltMaximum = ReadPoint(Required(bolt, "loadedPositionMax"));
            BoltQMinimum = Number(Required(bolt, "loadedQMin"));
            BoltQMaximum = Number(Required(bolt, "loadedQMax"));
            BoltHash = bolt["boltObjSha256"] == null ? null : Hash(bolt["boltObjSha256"]);
            if (BoltQMinimum > BoltQMaximum || BoltMinimum.X > BoltMaximum.X ||
                BoltMinimum.Y > BoltMaximum.Y || BoltMinimum.Z > BoltMaximum.Z)
                throw new InvalidDataException("Invalid loaded bolt domain.");
        }

        internal bool Deforms(string part) => part == FramePart || part == StringPart;

        internal bool TryGeometry(double q, double nativeSpan, Point[] centers, out Point[] nodes)
        {
            nodes = null;
            var half = (SpanBase + SpanRange * (nativeSpan - NativeSpanLoaded) /
                (NativeSpanMaximum - NativeSpanLoaded)) * 0.5;
            var nock = NockLoaded + NockTravel * q;
            var radicand = CordLength * CordLength * 0.25 - half * half - (TipY - nock.Y) * (TipY - nock.Y);
            if (!(radicand >= 0) || !(half > 0) || double.IsNaN(q) || double.IsInfinity(q) ||
                double.IsNaN(nativeSpan) || double.IsInfinity(nativeSpan)) return false;
            var z = nock.Z + Math.Sqrt(radicand);
            nodes = [new Point(-half, TipY, z), nock, new Point(half, TipY, z)];
            var target = new Point(half, TipY, z + TipOffset);
            var seed = Seeds[0];
            var distance = double.PositiveInfinity;
            foreach (var candidate in Seeds)
            {
                var d = Math.Abs(candidate.Q - q) + Math.Abs((candidate.Span - nativeSpan) /
                    (NativeSpanMaximum - NativeSpanLoaded));
                if (d >= distance) continue;
                distance = d;
                seed = candidate;
            }
            var a = seed.A;
            var b = seed.B;
            var step = Math.Sqrt(MaterialLength * MaterialLength - (TipY - Root.Y) * (TipY - Root.Y)) / Segments;
            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                var x = Root.X;
                var zz = Root.Z;
                double j00 = 0, j01 = 0, j10 = 0, j11 = 0;
                for (var i = 0; i < Segments; i++)
                {
                    var t = (i + 0.5) / Segments;
                    var s1 = Math.Sin(Math.PI * t);
                    var s2 = Math.Sin(2 * Math.PI * t);
                    var angle = a * s1 + b * s2;
                    x += step * Math.Cos(angle); zz += step * Math.Sin(angle);
                    j00 -= step * Math.Sin(angle) * s1; j01 -= step * Math.Sin(angle) * s2;
                    j10 += step * Math.Cos(angle) * s1; j11 += step * Math.Cos(angle) * s2;
                }
                var ex = x - target.X;
                var ez = zz - target.Z;
                var error = Math.Sqrt(ex * ex + ez * ez);
                if (error <= Residual)
                {
                    FillCenters(a, b, centers);
                    return true;
                }
                var determinant = j00 * j11 - j01 * j10;
                if (Math.Abs(determinant) < 1e-14) return false;
                var da = (ex * j11 - ez * j01) / determinant;
                var db = (j00 * ez - j10 * ex) / determinant;
                var accepted = false;
                for (var damping = 1d; damping >= 0.03125; damping *= 0.5)
                {
                    var end = Endpoint(a - da * damping, b - db * damping);
                    if ((end - target).Length >= error) continue;
                    a -= da * damping; b -= db * damping; accepted = true; break;
                }
                if (!accepted) return false;
            }
            return false;
        }

        private Point Endpoint(double a, double b)
        {
            var result = Root;
            var dy = (TipY - Root.Y) / Segments;
            var step = Math.Sqrt(MaterialLength * MaterialLength - (TipY - Root.Y) * (TipY - Root.Y)) / Segments;
            for (var i = 0; i < Segments; i++)
            {
                var t = (i + 0.5) / Segments;
                var angle = a * Math.Sin(Math.PI * t) + b * Math.Sin(2 * Math.PI * t);
                result += new Point(step * Math.Cos(angle), dy, step * Math.Sin(angle));
            }
            return result;
        }
        private void FillCenters(double a, double b, Point[] centers)
        {
            centers[0] = Root;
            var dy = (TipY - Root.Y) / Segments;
            var step = Math.Sqrt(MaterialLength * MaterialLength - (TipY - Root.Y) * (TipY - Root.Y)) / Segments;
            for (var i = 0; i < Segments; i++)
            {
                var t = (i + 0.5) / Segments;
                var angle = a * Math.Sin(Math.PI * t) + b * Math.Sin(2 * Math.PI * t);
                centers[i + 1] = centers[i] + new Point(step * Math.Cos(angle), dy, step * Math.Sin(angle));
            }
        }
        internal Point RingNormal(Point[] centers, int section, bool left)
        {
            if (section == 0 || section == Segments) return new Point(0, 0, left ? -1 : 1);
            var tangent = centers[section + 1] - centers[section - 1];
            return new Point(-tangent.Z, 0, left ? -tangent.X : tangent.X).Unit;
        }
        internal static (Point Tangent, Point Up, Point Side) StringFrame(Point[] nodes, int ring)
        {
            var t = (nodes[Math.Min(2, ring + 1)] - nodes[Math.Max(0, ring - 1)]).Unit;
            var y = new Point(0, 1, 0);
            var up = (y - t * y.Dot(t)).Unit;
            return (t, up, t.Cross(up));
        }
    }

    // Declarations keep source identity values, not a native factory asset lease.
    internal sealed class PlateBackSurface
    {
        private readonly int _columns, _rows;
        private readonly float _originY, _originZ, _stepY, _stepZ;
        private readonly float[,][] _maxima;
        private readonly float[] _depths;

        internal PlateBackSurface(JToken data)
        {
            _columns = GripInteger(GripField(data, "columns"));
            _rows = GripInteger(GripField(data, "rows"));
            var origin = GripArray(data, "origin", 2);
            var step = GripArray(data, "cellSize", 2);
            _originY = GripNumber(origin[0]); _originZ = GripNumber(origin[1]);
            _stepY = GripNumber(step[0]); _stepZ = GripNumber(step[1]);
            if (_columns is < 1 or > 64 || _rows is < 1 or > 64 || _stepY <= 0f || _stepZ <= 0f)
                throw new InvalidDataException("Invalid plate clearance grid.");
            _depths = GripArray(data, "depths", _columns * _rows)
                .Select(value => value.Type == JTokenType.Null ? float.NegativeInfinity : GripNumber(value)).ToArray();
            if (_depths.All(float.IsNegativeInfinity)) throw new InvalidDataException("Empty plate clearance grid.");
            var xLevels = Log2(_columns) + 1; var yLevels = Log2(_rows) + 1;
            _maxima = new float[xLevels, yLevels][];
            _maxima[0, 0] = _depths;
            for (var x = 1; x < xLevels; x++)
            {
                var values = new float[_depths.Length]; var previous = _maxima[x - 1, 0];
                for (var row = 0; row < _rows; row++)
                for (var column = 0; column < _columns; column++)
                    values[row * _columns + column] = Mathf.Max(previous[row * _columns + column],
                        previous[row * _columns + Mathf.Min(_columns - 1, column + (1 << (x - 1)))]);
                _maxima[x, 0] = values;
            }
            for (var y = 1; y < yLevels; y++)
            for (var x = 0; x < xLevels; x++)
            {
                var values = new float[_depths.Length]; var previous = _maxima[x, y - 1];
                for (var row = 0; row < _rows; row++)
                for (var column = 0; column < _columns; column++)
                    values[row * _columns + column] = Mathf.Max(previous[row * _columns + column],
                        previous[Mathf.Min(_rows - 1, row + (1 << (y - 1))) * _columns + column]);
                _maxima[x, y] = values;
            }
        }

        private static int Log2(int value)
        {
            var result = 0;
            while ((1 << (result + 1)) <= value) result++;
            return result;
        }

        internal bool SameAs(PlateBackSurface other) => other != null &&
            _columns == other._columns && _rows == other._rows && _originY == other._originY &&
            _originZ == other._originZ && _stepY == other._stepY && _stepZ == other._stepZ &&
            _depths.SequenceEqual(other._depths);

        internal bool TryGetDepth(Vector3 point, float padding, out float depth)
        {
            depth = float.NegativeInfinity;
            if (point.y + padding < _originY || point.y - padding > _originY + _columns * _stepY ||
                point.z + padding < _originZ || point.z - padding > _originZ + _rows * _stepZ) return false;
            var x1 = Mathf.Clamp(Mathf.FloorToInt((point.y - padding - _originY) / _stepY), 0, _columns - 1);
            var x2 = Mathf.Clamp(Mathf.FloorToInt((point.y + padding - _originY) / _stepY), 0, _columns - 1);
            var y1 = Mathf.Clamp(Mathf.FloorToInt((point.z - padding - _originZ) / _stepZ), 0, _rows - 1);
            var y2 = Mathf.Clamp(Mathf.FloorToInt((point.z + padding - _originZ) / _stepZ), 0, _rows - 1);
            var x = Log2(x2 - x1 + 1); var y = Log2(y2 - y1 + 1);
            var values = _maxima[x, y]; x2 -= (1 << x) - 1; y2 -= (1 << y) - 1;
            depth = Mathf.Max(Mathf.Max(values[y1 * _columns + x1], values[y1 * _columns + x2]),
                Mathf.Max(values[y2 * _columns + x1], values[y2 * _columns + x2]));
            return !float.IsNegativeInfinity(depth);
        }
    }

    // A native item's measured grip frame. This declares the original geometry;
    // the presentation owner only changes the item's rigid pose, never its mesh.
    internal sealed class WieldedGrip(string slot, string nativeAssetGuid, string meshPath,
        string meshName, int vertexCount, int subMeshCount, Bounds bounds,
        Vector3 gripPoint, Vector3 gripAxis, Vector3 palmNormal, Vector3? forearmSupportPoint = null,
        PlateBackSurface plateSurface = null)
    {
        internal string Slot { get; } = slot;
        internal string NativeAssetGuid { get; } = nativeAssetGuid;
        internal string MeshPath { get; } = meshPath;
        internal string MeshName { get; } = meshName;
        internal int VertexCount { get; } = vertexCount;
        internal int SubMeshCount { get; } = subMeshCount;
        internal Bounds OriginalBounds { get; } = bounds;
        internal Vector3 GripPoint { get; } = gripPoint;
        internal Vector3 GripAxis { get; } = gripAxis;
        // From the native bar toward the palm, not the palm's facing direction.
        internal Vector3 PalmNormal { get; } = palmNormal;
        internal Vector3? ForearmSupportPoint { get; } = forearmSupportPoint;
        internal PlateBackSurface PlateSurface { get; } = plateSurface;
    }

    // Native cloth owns both its immutable source and its current deformation
    // mesh. Read that ownership without invoking its Deformer getter, which sets
    // Parent. No cloth mesh, component or simulation state belongs to this owner.
    internal sealed class SkinMeshSource(SkinnedMeshRenderer renderer, Mesh original,
        Component cloth = null, object deformer = null)
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type ClothType = Type.GetType("MagicaCloth.MagicaRenderDeformer, MagicaCloth", false);
        private static readonly Type DeformerType = Type.GetType("MagicaCloth.RenderMeshDeformer, MagicaCloth", false);
        private static readonly FieldInfo TargetRenderer = ClothType?.GetField("targetRenderer", Fields);
        private static readonly FieldInfo Deformer = ClothType?.GetField("deformer", Fields);
        private static readonly FieldInfo SourceMesh = DeformerType?.GetField("sharedMesh", Fields);
        private static readonly FieldInfo RuntimeMesh = DeformerType?.GetField("mesh", Fields);
        private static readonly FieldInfo SkinRenderer = DeformerType?.GetField("skinMeshRenderer", Fields);

        internal Mesh Original { get; } = original;
        internal Component ClothOwner => cloth;
        internal object ClothDeformer => deformer;
        internal Mesh CurrentRuntimeMesh => deformer == null ? null : RuntimeMesh.GetValue(deformer) as Mesh;
        internal bool OwnerCurrent => deformer == null || cloth &&
            (TargetRenderer.GetValue(cloth) as Renderer) == renderer &&
            ReferenceEquals(Deformer.GetValue(cloth), deformer) &&
            (SourceMesh.GetValue(deformer) as Mesh) == Original &&
            (SkinRenderer.GetValue(deformer) is not SkinnedMeshRenderer target || target == renderer);

        internal bool AcceptsCurrentMesh()
        {
            if (!renderer || !Original || !OwnerCurrent)
            {
                return false;
            }
            var actual = renderer.sharedMesh;
            return actual && (actual == Original || deformer != null &&
                       (SkinRenderer.GetValue(deformer) as SkinnedMeshRenderer) == renderer &&
                       actual == CurrentRuntimeMesh) &&
                   actual.vertexCount == Original.vertexCount && actual.subMeshCount == Original.subMeshCount &&
                   actual.blendShapeCount == Original.blendShapeCount;
        }

        internal bool TrySetClothSourceMesh(Mesh replacement)
        {
            if (!replacement || deformer == null || !OwnerCurrent || !AcceptsCurrentMesh() ||
                replacement.vertexCount != Original.vertexCount ||
                replacement.subMeshCount != Original.subMeshCount)
            {
                return false;
            }

            SourceMesh.SetValue(deformer, replacement);
            return ReferenceEquals(SourceMesh.GetValue(deformer), replacement);
        }

        internal static Dictionary<SkinnedMeshRenderer, SkinMeshSource> Capture(GraphicsCharacter graphics,
            IEnumerable<SkinnedMeshRenderer> renderers)
        {
            var result = renderers.ToDictionary(renderer => renderer,
                renderer => new SkinMeshSource(renderer, renderer.sharedMesh));
            if (ClothType == null)
            {
                return result;
            }
            if (TargetRenderer == null || Deformer == null || SourceMesh == null || RuntimeMesh == null ||
                SkinRenderer == null)
            {
                return null;
            }
            var found = new HashSet<SkinnedMeshRenderer>();
            foreach (var component in graphics.GetComponentsInChildren(ClothType, true))
            {
                if (!component || TargetRenderer.GetValue(component) is not SkinnedMeshRenderer target ||
                    !result.ContainsKey(target))
                {
                    continue;
                }
                if (!found.Add(target) || Deformer.GetValue(component) is not { } source ||
                    !DeformerType.IsInstanceOfType(source) || SourceMesh.GetValue(source) is not Mesh mesh || !mesh)
                {
                    return null;
                }
                var entry = new SkinMeshSource(target, mesh, component, source);
                if (!entry.AcceptsCurrentMesh())
                {
                    return null;
                }
                result[target] = entry;
            }
            return result;
        }
    }

    private static HumanBodyBones[] HandFingerBones(string slot)
    {
        var side = slot == EquipmentDefinitions.SlotTypeOffHand ? "Left" : "Right";
        return Enum.GetValues(typeof(HumanBodyBones)).Cast<HumanBodyBones>().Where(bone =>
        {
            var name = bone.ToString();
            return name.StartsWith(side, StringComparison.Ordinal) &&
                   (name.Contains("Thumb") || name.Contains("Index") || name.Contains("Middle") ||
                    name.Contains("Ring") || name.Contains("Little"));
        }).ToArray();
    }

    private static string RelativePath(Transform child, Transform root)
    {
        var names = new List<string>();
        for (var current = child; current && current != root; current = current.parent)
        {
            names.Add(current.name);
        }
        names.Reverse();
        return string.Join("/", names);
    }

    internal static T GetWieldedItemFeature<T>(ItemDefinition definition) where T : class =>
        definition.GetFirstSubFeatureOfType<T>() ??
        (definition is { IsWeapon: true }
            ? definition.WeaponDescription.WeaponTypeDefinition.GetFirstSubFeatureOfType<T>()
            : null) ?? GetNativeSourceGrip<T>(definition);

    internal static IReadOnlyList<T> GetWieldedItemFeatures<T>(ItemDefinition definition) where T : class
    {
        if (!definition)
        {
            return Array.Empty<T>();
        }
        var features = definition.GetAllSubFeaturesOfType<T>();
        if (features.Count == 0 && definition.IsWeapon)
        {
            features = definition.WeaponDescription.WeaponTypeDefinition.GetAllSubFeaturesOfType<T>();
        }
        if (features.Count != 0)
        {
            return features;
        }
        var sourceGrip = GetNativeSourceGrip<T>(definition);
        return sourceGrip != null ? new[] { sourceGrip } : features;
    }

    // A measured grip describes the source prefab's geometry. Resolve it when
    // an item is used, including definitions created after source data loaded.
    private static T GetNativeSourceGrip<T>(ItemDefinition definition) where T : class =>
        typeof(T) == typeof(WieldedGrip) && definition
            ? BlenderModelLoader.Shared.FindWieldedGrip(definition.ItemPresentation?.AssetReference?.AssetGUID) as T
            : null;

    internal static Animator GetWieldedWeaponAnimator(GraphicsCharacter graphics, string slot)
    {
        if (!graphics || !graphics.WieldedInstantiatedItems.TryGetValue(slot, out var item) || !item)
        {
            return null;
        }
        var presentation = item.GetComponent<WieldedModelPresentation>();
        var model = presentation ? presentation.Model : null;
        foreach (var animator in item.GetComponentsInChildren<Animator>(true))
        {
            if (!model || !animator.transform.IsChildOf(model))
            {
                return animator;
            }
        }
        return null;
    }

    internal static bool HasWieldedHandPose(GraphicsCharacter graphics, string slot)
    {
        if (!graphics ||
            slot != EquipmentDefinitions.SlotTypeMainHand && slot != EquipmentDefinitions.SlotTypeOffHand)
        {
            return false;
        }

        // Ownership begins with the declaration, before a deferred grip has sampled
        // its native baseline. A later body pose must not contaminate that baseline.
        if (graphics.WieldedRulesetItems.TryGetValue(slot, out var item) && item != null &&
            (GetWieldedItemFeature<SphericalGrip>(item.ItemDefinition) != null ||
             GetWieldedItemFeature<WieldedModel>(item.ItemDefinition) != null ||
             GetWieldedItemFeature<WieldedGrip>(item.ItemDefinition)?.Slot == slot))
        {
            return true;
        }

        var animator = graphics.Animator;
        var layer = slot == EquipmentDefinitions.SlotTypeMainHand
            ? graphics.TorchRightLayerIndex
            : graphics.TorchLeftLayerIndex;
        return animator && layer >= 0 && layer < animator.layerCount && animator.GetLayerWeight(layer) > 0.0001f;
    }

    internal static bool TryGetWieldedModel(GraphicsCharacter graphics, string slot,
        out Transform model, out WieldedModel definition)
    {
        model = null;
        definition = null;
        if (!graphics || !graphics.WieldedInstantiatedItems.TryGetValue(slot, out var item) || !item)
        {
            return false;
        }

        var presentation = item.GetComponent<WieldedModelPresentation>();
        if (!presentation || !presentation.IsCurrent)
        {
            return false;
        }

        model = presentation.Model;
        definition = presentation.Definition;
        return model;
    }

    internal static void SetWieldedModelPose(GraphicsCharacter graphics, string slot,
        Vector3 position, Quaternion rotation)
    {
        if (TryGetWieldedModel(graphics, slot, out var model, out _))
        {
            var presentation = model.parent.GetComponent<WieldedModelPresentation>();
            if (!presentation.IsPoseReady)
            {
                return;
            }
            model.SetPositionAndRotation(position, rotation);
            presentation.Show();
        }
    }

    internal static bool IsWieldedItemPresentationReady(GraphicsCharacter graphics, string slot)
    {
        if (!graphics || slot != EquipmentDefinitions.SlotTypeMainHand && slot != EquipmentDefinitions.SlotTypeOffHand ||
            !graphics.WieldedInstantiatedItems.TryGetValue(slot, out var item) || !item)
        {
            return false;
        }
        var presentation = item.GetComponent<WieldedModelPresentation>();
        return presentation && presentation.isActiveAndEnabled && presentation.IsCurrent && presentation.IsPoseReady;
    }

    internal static bool SetWieldedItemPose(GraphicsCharacter graphics, string slot, Matrix4x4 worldPose)
    {
        return graphics && graphics.WieldedInstantiatedItems.TryGetValue(slot, out var item) && item &&
               item.GetComponent<WieldedModelPresentation>()?.SetPose(worldPose) == true;
    }

    internal static bool RequiresEquipmentPoseReferences(GraphicsCharacter graphics)
    {
        foreach (var pair in graphics.WieldedRulesetItems)
        {
            if (GetWieldedItemFeature<WieldedModel>(pair.Value?.ItemDefinition) != null ||
                GetWieldedItemFeature<WieldedGrip>(pair.Value?.ItemDefinition)?.Slot == pair.Key)
            {
                return true;
            }
        }

        return false;
    }

    internal static void ApplyWieldedItemPresentation(GraphicsCharacter graphics, RulesetItem item, string slot)
    {
        if (slot != EquipmentDefinitions.SlotTypeMainHand && slot != EquipmentDefinitions.SlotTypeOffHand ||
            !graphics.WieldedInstantiatedItems.TryGetValue(slot, out var model) || !model)
        {
            return;
        }

        var authored = GetWieldedItemFeature<WieldedModel>(item.ItemDefinition);
        var nativeGrip = GetWieldedItemFeature<WieldedGrip>(item.ItemDefinition);
        nativeGrip = nativeGrip?.Slot == slot ? nativeGrip : null;
        if (authored != null || nativeGrip != null)
        {
            var attachment = model.GetComponent<WieldedModelPresentation>() ??
                             model.AddComponent<WieldedModelPresentation>();
            if (attachment.enabled)
            {
                attachment.Bind(graphics, item, slot, authored, nativeGrip);
            }
        }

        var grip = GetWieldedItemFeature<SphericalGrip>(item.ItemDefinition);
        if (grip == null)
        {
            return;
        }

        var presentation = model.GetComponent<SphericalGripPresentation>() ??
                           model.AddComponent<SphericalGripPresentation>();
        presentation.Bind(graphics, slot, grip);
    }

    internal static void RefreshWieldedItemPresentations(GraphicsCharacter graphics)
    {
        foreach (var pair in graphics.WieldedRulesetItems)
        {
            if (pair.Value != null && graphics.WieldedInstantiatedItems.TryGetValue(pair.Key, out var model) && model)
            {
                var authored = model.GetComponent<WieldedModelPresentation>();
                if (authored && !authored.IsCurrent)
                {
                    authored.Release();
                }
                ApplyWieldedItemPresentation(graphics, pair.Value, pair.Key);
                model.GetComponent<WieldedModelPresentation>()?.RequestRefresh();
                model.GetComponent<SphericalGripPresentation>()?.RequestRefresh();
            }
        }
    }

    internal static void UpdateWieldedItemPresentations(GraphicsCharacter graphics)
    {
        if (WieldedModels.Count == 0)
        {
            return;
        }
        foreach (var model in graphics.WieldedInstantiatedItems.Values)
        {
            if (model)
            {
                var presentation = model.GetComponent<WieldedModelPresentation>();
                if (presentation && presentation.isActiveAndEnabled)
                {
                    presentation.UpdatePresentation();
                }
            }
        }
    }

    internal static void ReapplyEquipmentPoses(GraphicsCharacter graphics)
    {
        UpdateWieldedItemPresentations(graphics);
        foreach (var model in graphics.WieldedInstantiatedItems.Values)
        {
            var spherical = model ? model.GetComponent<SphericalGripPresentation>() : null;
            if (spherical && spherical.isActiveAndEnabled) spherical.UpdatePresentation();
        }
    }

    internal static void ReleaseWieldedItemPresentations(GraphicsCharacter graphics)
    {
        if (WieldedModels.Count == 0)
        {
            return;
        }
        foreach (var presentation in WieldedModels.ToArray())
        {
            if (presentation && presentation.Owner == graphics)
            {
                presentation.Release();
            }
        }
    }

    private sealed class WieldedModelPresentation : MonoBehaviour
    {
        private GraphicsCharacter _graphics;
        private RulesetCharacter _actor;
        private RulesetItem _item;
        private string _slot;
        private Animator _animator;
        private Avatar _avatar;
        private WieldedGrip _gripDefinition;
        private MeshFilter _nativeMeshFilter;
        private Mesh _nativeOriginalMesh;
        private Mesh _foreignMesh;
        private MeshFilter _foreignFilter;
        private string _nativeKey;
        private bool _gripReady;
        private float _modelNormalSign;
        private float _nativeGripAxisSign;
        private SkinnedMeshRenderer[] _armSkins;
        private int _armShapeSignature;
        private readonly Dictionary<HumanBodyBones, float> _segmentRadii = [];
        private readonly struct SkinPoint(Transform bone, Vector3 local)
        {
            internal readonly Transform Bone = bone;
            internal readonly Vector3 Local = local;
        }
        private SkinPoint[] _armEnvelope;
        private Vector3[] _armInParent;
        private float _plateRoll;
        internal float PlateClearanceGap { get; private set; }
        private Transform _carryUpperArm;
        private Quaternion _carryOriginalRotation, _carryAppliedRotation;
        private bool _carryPoseApplied, _carryAngleInitialized;
        private int _carryFrame = -1;
        private float _carryAngle;
        private Vector3[] _carryPoints;
        internal Transform CarryUpperArm => _carryUpperArm;
        internal Quaternion CarryOriginalRotation => _carryOriginalRotation;
        internal Quaternion CarryAppliedRotation => _carryAppliedRotation;
        internal float CarryAngle => _carryAngle;
        internal bool CarryPoseApplied => _carryPoseApplied;
        internal WieldedGrip Grip => _gripDefinition;
        internal Mesh NativeMesh => _nativeOriginalMesh;
        internal string GripStatus { get; private set; } = "Unbound";
        private Transform[] _fingerBones;
        private Quaternion[] _fingerTargets;
        private Quaternion[] _fingerOriginals;
        private Quaternion[] _fingerApplied;
        private RuntimeAnimatorController _poseController;
        private RuntimeAnimatorController _poseSource;
        private bool _poseResolutionAttempted;
        private bool _fingersApplied;
        private Transform _hand;
        private Transform _lowerArm;
        private Vector3 _originalPosition;
        private Vector3 _appliedPosition;
        private Quaternion _originalRotation;
        private Quaternion _appliedRotation;
        private Vector3 _originalScale;
        private Vector3 _appliedScale;
        private bool _scalePoseApplied;
        private bool _itemPoseApplied;
        private Transform _posedTransform;
        private Renderer[] _nativeRenderers;
        private bool[] _nativeHidden;
        private Animator _weaponAnimator;
        private RuntimeAnimatorController _weaponController;
        private Transform[] _weaponBones;
        private Transform[] _weaponParents;
        private Transform _weaponBolt;
        private Transform[] _mechanismBolts;
        private MeshFilter _frameFilter;
        private MeshFilter _stringFilter;
        private Mesh _frameOriginal;
        private Mesh _stringOriginal;
        private Mesh _frameMesh;
        private Mesh _stringMesh;
        private Vector3[] _frameVertices, _frameNormals, _frameBaseNormals;
        private Vector4[] _frameTangents, _frameBaseTangents;
        private Vector3[] _stringVertices, _stringNormals, _stringBaseNormals;
        private Vector4[] _stringTangents, _stringBaseTangents;
        private WieldedMechanism.Point[] _centers, _loadedCenters, _loadedStringNodes;
        private bool _mechanismReady;
        private bool _mechanismFailed;
        private bool _weaponCullingLease;
        private AnimatorCullingMode _weaponOriginalCulling;
        internal string MechanismStatus { get; private set; } = "None";
        internal GraphicsCharacter Owner => _graphics;
        internal Transform Model { get; private set; }
        internal WieldedModel Definition { get; private set; }
        internal bool IsPoseReady => _gripReady && _fingersApplied && NativeGripCurrent() &&
                                     (Definition?.Mechanism == null || _mechanismReady && MechanismCurrent());
        private bool SameActor => _graphics && _animator && _graphics.Animator == _animator &&
                                  ReferenceEquals(_actor, _graphics.RulesetCharacter);
        private bool SameRig => SameActor && _avatar == _animator.avatar;
        internal bool IsCurrent => SameRig && CombatAnimationContext.IsEquipmentControllerCurrent(_graphics) &&
                                   _nativeKey == _graphics.CurrentAnimatorController.Key &&
                                   _graphics.WieldedRulesetItems.TryGetValue(_slot, out var item) &&
                                   ReferenceEquals(item, _item) &&
                                   _graphics.WieldedInstantiatedItems.TryGetValue(_slot, out var model) &&
                                   model == gameObject &&
                                   MatchesDeclaration(item.ItemDefinition);

        private bool MatchesDeclaration(ItemDefinition item)
        {
            var grip = GetWieldedItemFeature<WieldedGrip>(item);
            return ReferenceEquals(GetWieldedItemFeature<WieldedModel>(item), Definition) &&
                   ReferenceEquals(grip?.Slot == _slot ? grip : null, _gripDefinition);
        }

        private bool NativeGripCurrent() => _gripDefinition == null ||
            _nativeMeshFilter && _nativeOriginalMesh && _nativeMeshFilter.sharedMesh == _nativeOriginalMesh &&
            NativeMeshMatches(_nativeOriginalMesh, _gripDefinition);

        private static bool NativeMeshMatches(Mesh mesh, WieldedGrip grip) => mesh &&
            mesh.name == grip.MeshName && mesh.vertexCount == grip.VertexCount &&
            mesh.subMeshCount == grip.SubMeshCount &&
            (mesh.bounds.center - grip.OriginalBounds.center).sqrMagnitude <= 0.000000000001f &&
            (mesh.bounds.size - grip.OriginalBounds.size).sqrMagnitude <= 0.000000000001f;

        internal void Bind(GraphicsCharacter graphics, RulesetItem item, string slot, WieldedModel definition,
            WieldedGrip grip)
        {
            if (IsCurrent)
            {
                return;
            }

            Release();
            if (!graphics.Animator || !CombatAnimationContext.IsEquipmentControllerCurrent(graphics) ||
                definition != null && grip != null)
            {
                return;
            }
            _graphics = graphics;
            _actor = graphics.RulesetCharacter;
            _item = item;
            _slot = slot;
            _animator = graphics.Animator;
            _avatar = _animator.avatar;
            _nativeKey = graphics.CurrentAnimatorController.Key;
            Definition = definition;
            _gripDefinition = grip;
            GripStatus = "AwaitingNativeFingers";
            WieldedModels.Add(this);

            if (grip != null)
            {
                var target = string.IsNullOrEmpty(grip.MeshPath) || grip.MeshPath == "."
                    ? transform
                    : transform.Find(grip.MeshPath);
                _nativeMeshFilter = target ? target.GetComponent<MeshFilter>() : null;
                var original = _nativeMeshFilter ? _nativeMeshFilter.sharedMesh : null;
                if (!_nativeMeshFilter || !target.GetComponent<MeshRenderer>() ||
                    !NativeMeshMatches(original, grip) ||
                    _nativeMeshFilter == _foreignFilter && original == _foreignMesh ||
                    !string.Equals(item.ItemDefinition.ItemPresentation?.AssetReference?.AssetGUID,
                        grip.NativeAssetGuid, StringComparison.OrdinalIgnoreCase))
                {
                    Release();
                    return;
                }
                _nativeOriginalMesh = original;
            }

            if (definition == null)
            {
                return;
            }
            // Replace the visible body, retaining native particle, trail and line
            // effects and mesh renderers owned by a nested particle system.
            _nativeRenderers = Array.FindAll(GetComponentsInChildren<Renderer>(true), renderer =>
            {
                if (renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer)
                {
                    return false;
                }

                for (var parent = renderer.transform; parent && parent != transform; parent = parent.parent)
                {
                    if (parent.GetComponent("ParticleSystem"))
                    {
                        return false;
                    }
                }

                return true;
            });
            _nativeHidden = Array.ConvertAll(_nativeRenderers, renderer => renderer.forceRenderingOff);
            Model = new GameObject("UB_WieldedModel").transform;
            Model.SetParent(transform, false);
            Model.gameObject.SetActive(false);
            foreach (var part in definition.Parts)
            {
                var reference = definition.Mechanism?.Deforms(part) == true
                    ? GetOrderedBlenderModel(part)
                    : GetBlenderModel(part);
                var prefab = GetPrefabByGuid(reference.AssetGUID);
                if (!prefab)
                {
                    Release();
                    return;
                }

                var instance = UnityEngine.Object.Instantiate(prefab, Model, false);
                instance.name = part;
                foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.gameObject.layer = graphics.gameObject.layer;
                }
            }
        }

        internal bool Show()
        {
            if (!IsCurrent)
            {
                Release();
                return false;
            }
            if (!IsPoseReady || !Model)
            {
                return false;
            }

            foreach (var renderer in _nativeRenderers)
            {
                if (renderer)
                {
                    renderer.forceRenderingOff = true;
                }
            }

            if (!Model.gameObject.activeSelf)
            {
                Model.gameObject.SetActive(true);
                _graphics.RefreshRenderers();
            }
            return true;
        }

        internal void RequestRefresh()
        {
            _armSkins = null;
            _armShapeSignature = 0;
            _segmentRadii.Clear();
            _armEnvelope = null;
            RestoreFingers();
            _fingerBones = null;
            _poseResolutionAttempted = false;
            _poseSource = null;
            _gripReady = false;
        }

        internal bool SetPose(Matrix4x4 worldPose)
        {
            if (!IsCurrent || !IsPoseReady || !gameObject.activeInHierarchy)
            {
                return false;
            }
            // The frame belongs to an authored display child or the unchanged
            // native item root. No source mesh is replaced by an attachment.
            // An authored model never moves the native weapon rig or FX hierarchy.
            var target = Model;
            if (!target)
            {
                if (_gripDefinition == null || !NativeGripCurrent())
                {
                    return false;
                }
                target = transform;
            }
            return ApplyPose(target, worldPose) && (!Model || Show());
        }

        private bool ApplyPose(Transform target, Matrix4x4 worldPose)
        {
            if (!target.parent)
            {
                return false;
            }
            var local = target.parent.worldToLocalMatrix * worldPose;
            var scale = target.localScale;
            if (scale.x == 0f || scale.y == 0f || scale.z == 0f ||
                float.IsNaN(scale.sqrMagnitude) || float.IsInfinity(scale.sqrMagnitude))
            {
                return false;
            }
            for (var i = 0; i < 16; i++)
            {
                if (float.IsNaN(local[i]) || float.IsInfinity(local[i]))
                {
                    return false;
                }
            }

            // Only the authored display child absorbs the actual native item
            // scale (including CustomScale applied after Bind). Native rig/FX
            // and unchanged native item roots retain their signed scale contract.
            var ownsScale = target == Model;
            if (ownsScale)
            {
                var signs = _scalePoseApplied && target.localScale.Equals(_appliedScale)
                    ? _originalScale : target.localScale;
                scale = new Vector3(((Vector3)local.GetColumn(0)).magnitude * Mathf.Sign(signs.x),
                    ((Vector3)local.GetColumn(1)).magnitude * Mathf.Sign(signs.y),
                    ((Vector3)local.GetColumn(2)).magnitude * Mathf.Sign(signs.z));
                if (scale.x == 0 || scale.y == 0 || scale.z == 0 ||
                    float.IsNaN(scale.sqrMagnitude) || float.IsInfinity(scale.sqrMagnitude)) return false;
                var determinant = local.determinant;
                if (determinant == 0f || float.IsNaN(determinant) || float.IsInfinity(determinant)) return false;
                // Preserve the local frame's parity on the owned child only.
                if (Mathf.Sign(determinant) != Mathf.Sign(scale.x * scale.y * scale.z)) scale.x = -scale.x;
            }

            // The native Shield helper has a reflected parent. Remove that full
            // matrix first, then use the item's existing signed local scale once.
            Vector3 forward = local.GetColumn(2) / scale.z;
            Vector3 up = local.GetColumn(1) / scale.y;
            if (forward.sqrMagnitude < 0.00000001f || Vector3.Cross(forward, up).sqrMagnitude < 0.00000001f)
            {
                return false;
            }
            var position = new Vector3(local.m03, local.m13, local.m23);
            var rotation = Quaternion.LookRotation(forward, up);
            var reconstructed = Matrix4x4.TRS(position, rotation, scale);
            for (var i = 0; i < 16; i++)
            {
                if (Mathf.Abs(reconstructed[i] - local[i]) > 0.0001f)
                {
                    return false;
                }
            }

            if (_posedTransform && _posedTransform != target)
            {
                RestoreItemPose();
            }
            _posedTransform = target;
            if (!_itemPoseApplied || !target.localPosition.Equals(_appliedPosition))
            {
                _originalPosition = target.localPosition;
            }
            if (!_itemPoseApplied || !target.localRotation.Equals(_appliedRotation))
            {
                _originalRotation = target.localRotation;
            }
            if (ownsScale)
            {
                if (!_scalePoseApplied || !target.localScale.Equals(_appliedScale))
                    _originalScale = target.localScale;
                target.localScale = scale;
                _appliedScale = target.localScale;
                _scalePoseApplied = true;
            }
            target.localPosition = position;
            target.localRotation = rotation;
            _appliedPosition = target.localPosition;
            _appliedRotation = target.localRotation;
            _itemPoseApplied = true;
            return true;
        }

        internal void UpdatePresentation()
        {
            RestoreCarryPose();
            if (!IsCurrent)
            {
                Release();
                return;
            }
            if (!NativeGripCurrent())
            {
                _foreignFilter = _nativeMeshFilter;
                _foreignMesh = _nativeMeshFilter ? _nativeMeshFilter.sharedMesh : null;
                Release();
                return;
            }

            var source = CombatAnimationContext.GetEquipmentReferenceController(_graphics);
            if (_poseController != _animator.runtimeAnimatorController || _poseSource != source)
            {
                RestoreFingers();
                _fingerBones = null;
                _poseResolutionAttempted = false;
                _poseController = _animator.runtimeAnimatorController;
                _poseSource = source;
            }
            if (!source || !_animator.enabled || !_animator.isInitialized || !gameObject.activeInHierarchy)
            {
                SuspendPresentation();
                return;
            }
            if (!_poseResolutionAttempted)
            {
                _poseResolutionAttempted = true;
                if (!ConfigureFingers(source))
                {
                    GripStatus = "NativeFingerSourceUnavailable";
                    SuspendPresentation();
                    return;
                }
            }
            if (_fingerBones == null)
            {
                return;
            }

            var torch = _slot == EquipmentDefinitions.SlotTypeMainHand
                ? _graphics.TorchRightLayerIndex
                : _graphics.TorchLeftLayerIndex;
            if (GetWieldedItemFeature<SphericalGrip>(_item.ItemDefinition) != null ||
                torch >= 0 && torch < _animator.layerCount && _animator.GetLayerWeight(torch) > 0.0001f)
            {
                SuspendPresentation();
                return;
            }
            for (var i = 0; i < _fingerBones.Length; i++)
            {
                if (!_fingersApplied || !_fingerBones[i].localRotation.Equals(_fingerApplied[i]))
                {
                    _fingerOriginals[i] = _fingerBones[i].localRotation;
                }
                _fingerBones[i].localRotation = _fingerTargets[i];
                _fingerApplied[i] = _fingerBones[i].localRotation;
            }
            _fingersApplied = true;
            if (!UpdateMechanism())
            {
                SuspendPresentation();
                return;
            }
            if (!ApplyGripPose())
            {
                GripStatus = "InvalidActualHandFrame";
                SuspendPresentation();
                return;
            }
            _gripReady = true;
            GripStatus = "Ready";
            if (Model && !Show()) SuspendPresentation();
        }

        private bool ApplyGripPose()
        {
            if (!_hand || !_lowerArm || _fingerBones == null || _fingerBones.Length != 15) return false;
            var center = Vector3.zero;
            var knuckles = Vector3.zero;
            var curl = Vector3.zero;
            var inverseHand = _hand.worldToLocalMatrix;
            for (var first = 3; first < 15; first += 3)
            {
                var proximal = inverseHand.MultiplyPoint3x4(_fingerBones[first].position);
                var intermediate = inverseHand.MultiplyPoint3x4(_fingerBones[first + 1].position);
                var distal = inverseHand.MultiplyPoint3x4(_fingerBones[first + 2].position);
                center += (proximal + distal) * 0.125f;
                knuckles += proximal * 0.25f;
                curl += (intermediate - proximal) * 0.25f;
            }
            var axis = inverseHand.MultiplyVector(_fingerBones[3].position - _fingerBones[12].position);
            var normal = Vector3.Cross(axis, knuckles);
            if (axis.sqrMagnitude < 0.00000001f || normal.sqrMagnitude < 0.000000000001f) return false;
            axis.Normalize();
            normal.Normalize();
            // The native ClosedHands MCP-to-PIP bend points toward the palm.
            // Source normals point from the bar toward that same palm side.
            if (Vector3.Dot(normal, curl) < 0f) normal = -normal;

            if (!Model)
            {
                if (_gripDefinition == null || !transform.parent || !NativeGripCurrent()) return false;
                var parent = transform.parent;
                var scale = transform.localScale;
                var sourceAxis = Vector3.Scale(_gripDefinition.GripAxis, scale);
                var sourceNormal = Vector3.Scale(_gripDefinition.PalmNormal, scale);
                var targetAxis = parent.InverseTransformVector(_hand.TransformVector(axis));
                var targetNormal = parent.InverseTransformVector(_hand.TransformVector(normal));
                if (!Frame(sourceAxis, sourceNormal, out var sourceFrame)) return false;
                if (_nativeGripAxisSign == 0f)
                {
                    _nativeGripAxisSign = 1f;
                    if (_gripDefinition.ForearmSupportPoint is { } support)
                    {
                        var sourceSupport = Vector3.Scale(support - _gripDefinition.GripPoint, scale);
                        var targetForearm = parent.InverseTransformVector(_lowerArm.position - _hand.position).normalized;
                        var best = float.NegativeInfinity;
                        foreach (var sign in new[] { 1f, -1f })
                        {
                            if (!Frame(targetAxis * sign, targetNormal, out var candidate)) return false;
                            var candidateRotation = candidate * Quaternion.Inverse(sourceFrame);
                            var score = Vector3.Dot((candidateRotation * sourceSupport).normalized, targetForearm);
                            if (score <= best) continue;
                            best = score;
                            _nativeGripAxisSign = sign;
                        }
                    }
                }
                // A bar axis is unoriented. Its measured support disambiguates
                // which end belongs toward the elbow; retain that sign while held.
                if (!Frame(targetAxis * _nativeGripAxisSign, targetNormal, out var targetFrame)) return false;
                var rotation = targetFrame * Quaternion.Inverse(sourceFrame);
                rotation = FitPlate(parent, center, scale, targetAxis * _nativeGripAxisSign, rotation);
                var position = parent.InverseTransformPoint(_hand.TransformPoint(center)) -
                               rotation * Vector3.Scale(_gripDefinition.GripPoint, scale);
                // Compose through the real native parent, including its reflection.
                // ApplyPose retains the native signed local scale without resizing.
                return ApplyPose(transform, parent.localToWorldMatrix * Matrix4x4.TRS(position, rotation, scale));
            }

            var modelAxis = Definition.GripAxis;
            var modelNormal = Vector3.Cross(modelAxis, Definition.RailAxis);
            if (!Frame(axis, normal, out var handFrame) || modelNormal.sqrMagnitude < 0.00000001f) return false;
            if (_modelNormalSign == 0f)
            {
                var forearm = inverseHand.MultiplyVector(_hand.position - _lowerArm.position).normalized;
                var best = float.NegativeInfinity;
                foreach (var sign in new[] { 1f, -1f })
                {
                    if (!Frame(modelAxis, modelNormal * sign, out var candidate)) return false;
                    var rotation = handFrame * Quaternion.Inverse(candidate);
                    var score = Vector3.Dot((rotation * Definition.RailAxis).normalized, forearm);
                    if (score <= best) continue;
                    best = score;
                    _modelNormalSign = sign;
                }
            }
            if (!Frame(modelAxis, modelNormal * _modelNormalSign, out var modelFrame)) return false;
            var handRotation = handFrame * Quaternion.Inverse(modelFrame);
            var handPosition = center - handRotation * Definition.GripPoint;
            ApplyCarryClearance(handPosition, handRotation);
            // Keep the rigid frame in hand coordinates. Its full native matrix
            // carries body scaling; the item-parent inverse cancels it before
            // ApplyPose removes the native weapon's separate display scale.
            return ApplyPose(Model, _hand.localToWorldMatrix *
                                    Matrix4x4.TRS(handPosition, handRotation, Vector3.one));
        }

        private static bool Frame(Vector3 axis, Vector3 normal, out Quaternion frame)
        {
            frame = default;
            if (axis.sqrMagnitude < 0.00000001f || normal.sqrMagnitude < 0.00000001f ||
                float.IsNaN(axis.sqrMagnitude) || float.IsInfinity(axis.sqrMagnitude) ||
                float.IsNaN(normal.sqrMagnitude) || float.IsInfinity(normal.sqrMagnitude)) return false;
            axis.Normalize();
            normal = Vector3.ProjectOnPlane(normal, axis);
            if (normal.sqrMagnitude < 0.00000001f) return false;
            frame = Quaternion.LookRotation(axis, normal);
            return true;
        }

        private void RefreshSkinCache()
        {
            _armSkins ??= _graphics.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var signature = 17;
            unchecked
            {
                foreach (var skin in _armSkins)
                {
                    if (!skin || !skin.enabled || !skin.gameObject.activeInHierarchy || !skin.sharedMesh) continue;
                    signature = signature * 31 + skin.sharedMesh.GetInstanceID();
                    var muscle = skin.sharedMesh.GetBlendShapeIndex("Muscular");
                    if (muscle >= 0) signature = signature * 31 + skin.GetBlendShapeWeight(muscle).GetHashCode();
                    var volume = skin.sharedMesh.GetBlendShapeIndex("UB_BodyVolumePositive");
                    if (volume >= 0) signature = signature * 31 + skin.GetBlendShapeWeight(volume).GetHashCode();
                    volume = skin.sharedMesh.GetBlendShapeIndex("UB_BodyVolumeNegative");
                    if (volume >= 0) signature = signature * 31 + skin.GetBlendShapeWeight(volume).GetHashCode();
                    signature = signature * 31 + skin.transform.lossyScale.GetHashCode();
                }
            }
            if (signature != _armShapeSignature) { _segmentRadii.Clear(); _armEnvelope = null; }
            _armShapeSignature = signature;
        }

        private float SegmentRadius(HumanBodyBones weightedBone, HumanBodyBones firstBone,
            HumanBodyBones lastBone, float minimumRatio)
        {
            RefreshSkinCache();
            if (_segmentRadii.TryGetValue(weightedBone, out var cached)) return cached;
            var first = _animator.GetBoneTransform(firstBone);
            var last = _animator.GetBoneTransform(lastBone);
            var weighted = _animator.GetBoneTransform(weightedBone);
            if (!first || !last || !weighted) return 0f;
            var arm = last.position - first.position;
            var length = arm.magnitude;
            var direction = arm / Mathf.Max(length, 0.0001f);
            var radius = length * minimumRatio;
            var leftLeg = weightedBone == HumanBodyBones.LeftUpperLeg;
            var legSegment = leftLeg || weightedBone == HumanBodyBones.RightUpperLeg;
            var hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
            var opposite = _animator.GetBoneTransform(leftLeg ? HumanBodyBones.RightUpperLeg : HumanBodyBones.LeftUpperLeg);
            var leftArm = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var rightArm = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            var snapshot = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                foreach (var skin in _armSkins)
                {
                    if (!skin || !skin.enabled || !skin.gameObject.activeInHierarchy || !skin.sharedMesh) continue;
                    var bones = skin.bones;
                    var bone = Array.IndexOf(bones, weighted);
                    if (!legSegment && bone < 0) continue;
                    var weights = skin.sharedMesh.boneWeights;
                    if (!legSegment && !weights.Any(weight => weight.boneIndex0 == bone && weight.weight0 >= 0.25f ||
                                               weight.boneIndex1 == bone && weight.weight1 >= 0.25f ||
                                               weight.boneIndex2 == bone && weight.weight2 >= 0.25f ||
                                               weight.boneIndex3 == bone && weight.weight3 >= 0.25f)) continue;
                    skin.BakeMesh(snapshot);
                    var vertices = snapshot.vertices;
                    if (vertices.Length != weights.Length) continue;
                    var toWorld = Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one);
                    for (var i = 0; i < vertices.Length; i++)
                    {
                        var weight = weights[i];
                        if (!legSegment && !(weight.boneIndex0 == bone && weight.weight0 >= 0.25f ||
                              weight.boneIndex1 == bone && weight.weight1 >= 0.25f ||
                              weight.boneIndex2 == bone && weight.weight2 >= 0.25f ||
                              weight.boneIndex3 == bone && weight.weight3 >= 0.25f)) continue;
                        var world = toWorld.MultiplyPoint3x4(vertices[i]);
                        if (legSegment)
                        {
                            var index = weight.boneIndex0; var maximum = weight.weight0;
                            if (weight.weight1 > maximum) { index = weight.boneIndex1; maximum = weight.weight1; }
                            if (weight.weight2 > maximum) { index = weight.boneIndex2; maximum = weight.weight2; }
                            if (weight.weight3 > maximum) index = weight.boneIndex3;
                            if (index < 0 || index >= bones.Length || !bones[index] || !hips) continue;
                            var source = bones[index];
                            if (source != hips && !source.IsChildOf(hips) ||
                                leftArm && (source == leftArm || source.IsChildOf(leftArm)) ||
                                rightArm && (source == rightArm || source.IsChildOf(rightArm))) continue;
                            if (opposite && (world - opposite.position).sqrMagnitude < (world - first.position).sqrMagnitude) continue;
                        }
                        var point = world - first.position;
                        var along = Vector3.Dot(point, direction);
                        if (along < length * (legSegment ? -0.1f : 0.2f) || along > length * 1.05f) continue;
                        radius = Mathf.Max(radius, (point - direction * along).magnitude);
                    }
                }
            }
            finally { Destroy(snapshot); }
            _segmentRadii[weightedBone] = radius;
            return radius;
        }

        private Quaternion FitPlate(Transform parent, Vector3 gripInHand, Vector3 scale, Vector3 axis,
            Quaternion original)
        {
            var surface = _gripDefinition.PlateSurface;
            if (surface == null) return original;
            RefreshSkinCache();
            if (_armEnvelope == null) CaptureArmEnvelope();
            if (_armEnvelope == null || _armEnvelope.Length == 0) return original;
            var gripWorld = _hand.TransformPoint(gripInHand);
            _armInParent ??= new Vector3[_armEnvelope.Length];
            if (_armInParent.Length != _armEnvelope.Length) _armInParent = new Vector3[_armEnvelope.Length];
            for (var i = 0; i < _armEnvelope.Length; i++)
                _armInParent[i] = parent.InverseTransformVector(
                    _armEnvelope[i].Bone.TransformPoint(_armEnvelope[i].Local) - gripWorld);
            axis.Normalize();
            var support = _gripDefinition.ForearmSupportPoint is { } supportPoint
                ? Vector3.Scale(supportPoint - _gripDefinition.GripPoint, scale) : Vector3.zero;
            var towardElbow = parent.InverseTransformVector(_lowerArm.position - _hand.position).normalized;
            float Gap(float roll)
            {
                var rotation = Quaternion.AngleAxis(roll, axis) * original;
                if (support.sqrMagnitude > 0f && Vector3.Dot((rotation * support).normalized, towardElbow) < 0.4f)
                    return float.NegativeInfinity;
                var inverse = Quaternion.Inverse(rotation);
                var worldUnit = parent.TransformVector(rotation * new Vector3(scale.x, 0, 0)).magnitude;
                if (worldUnit < 0.0001f) return float.NegativeInfinity;
                var minimum = float.PositiveInfinity;
                foreach (var point in _armInParent)
                {
                    var p = inverse * point;
                    p = _gripDefinition.GripPoint + new Vector3(p.x / scale.x, p.y / scale.y, p.z / scale.z);
                    if (surface.TryGetDepth(p, 0.004f / worldUnit, out var back))
                        minimum = Mathf.Min(minimum, (p.x - back) * worldUnit);
                }
                return minimum;
            }
            var best = Gap(_plateRoll);
            if (best < 0.006f)
            {
                var selected = _plateRoll;
                for (var delta = 1; delta <= 120; delta++)
                {
                    var found = false;
                    for (var sign = -1; sign <= 1; sign += 2)
                    {
                        var angle = _plateRoll + delta * sign;
                        if (angle < -60f || angle > 60f) continue;
                        var gap = Gap(angle);
                        if (gap > best) { best = gap; selected = angle; }
                        if (gap >= 0.01f) { found = true; break; }
                    }
                    if (found) break;
                }
                _plateRoll = selected;
            }
            PlateClearanceGap = best;
            return Quaternion.AngleAxis(_plateRoll, axis) * original;
        }

        private void CaptureArmEnvelope()
        {
            const int bins = 8, directions = 16;
            var left = _slot == EquipmentDefinitions.SlotTypeOffHand;
            var upper = _animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            var branch = _animator.GetBoneTransform(left ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder);
            if (!upper) return;
            if (!branch) branch = upper;
            var along = (upper.position - _hand.position).normalized;
            var side = Vector3.Cross(along, _graphics.transform.forward).normalized;
            if (side.sqrMagnitude < 0.01f) side = Vector3.Cross(along, _graphics.transform.right).normalized;
            var front = Vector3.Cross(along, side).normalized;
            var forearm = (_lowerArm.position - _hand.position).normalized;
            var length = (upper.position - _hand.position).magnitude +
                         (upper.position - _lowerArm.position).magnitude * 0.5f;
            var scores = Enumerable.Repeat(float.NegativeInfinity, bins * directions).ToArray();
            var samples = new SkinPoint[bins * directions];
            var snapshot = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                foreach (var skin in _armSkins)
                {
                    if (!skin || !skin.enabled || !skin.gameObject.activeInHierarchy || !skin.sharedMesh) continue;
                    var bones = skin.bones;
                    var weights = skin.sharedMesh.boneWeights;
                    if (!bones.Any(bone => bone && (bone == branch || bone.IsChildOf(branch)))) continue;
                    skin.BakeMesh(snapshot);
                    var vertices = snapshot.vertices;
                    if (vertices.Length != weights.Length) continue;
                    var matrix = Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one);
                    for (var i = 0; i < vertices.Length; i++)
                    {
                        var w = weights[i]; var index = w.boneIndex0; var weight = w.weight0;
                        if (w.weight1 > weight) { index = w.boneIndex1; weight = w.weight1; }
                        if (w.weight2 > weight) { index = w.boneIndex2; weight = w.weight2; }
                        if (w.weight3 > weight) { index = w.boneIndex3; weight = w.weight3; }
                        if (weight < 0.25f || index < 0 || index >= bones.Length) continue;
                        var bone = bones[index];
                        if (!bone || bone != branch && !bone.IsChildOf(branch)) continue;
                        var world = matrix.MultiplyPoint3x4(vertices[i]); var point = world - _hand.position;
                        // The grasping palm/fingers may contact their bar. The
                        // clearance envelope begins at the wrist and includes
                        // twist bones, cuffs, upper arm and shoulder armor.
                        if ((bone == _hand || bone.IsChildOf(_hand)) && Vector3.Dot(point, forearm) < 0f) continue;
                        var bin = Mathf.Clamp(Mathf.FloorToInt(Vector3.Dot(point, along) / Mathf.Max(length, 0.0001f) * bins), 0, bins - 1);
                        for (var d = 0; d < directions; d++)
                        {
                            var angle = d * Mathf.PI * 2f / directions;
                            var score = Vector3.Dot(point, side * Mathf.Cos(angle) + front * Mathf.Sin(angle));
                            var slot = bin * directions + d;
                            if (score <= scores[slot]) continue;
                            scores[slot] = score; samples[slot] = new SkinPoint(bone, bone.InverseTransformPoint(world));
                        }
                    }
                }
            }
            finally { Destroy(snapshot); }
            _armEnvelope = samples.Where(sample => sample.Bone).ToArray();
            _armInParent = null;
        }

        private void RestoreCarryPose()
        {
            if (_carryPoseApplied && SameRig && _carryUpperArm &&
                _carryUpperArm.localRotation.Equals(_carryAppliedRotation))
                _carryUpperArm.localRotation = _carryOriginalRotation;
            _carryPoseApplied = false;
        }

        private void ApplyCarryClearance(Vector3 handPosition, Quaternion handRotation)
        {
            if (_item.ItemDefinition.WeaponDescription?.WeaponTags.Contains(TagsDefinitions.WeaponTagTwoHanded) == true)
                return;
            var left = _slot == EquipmentDefinitions.SlotTypeOffHand;
            var upperArm = _animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            var thigh = _animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg);
            var knee = _animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
            var hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
            if (!upperArm || !thigh || !knee || !hips) return;
            _carryUpperArm = upperArm;
            if (_carryPoints == null)
            {
                var points = new List<Vector3>();
                foreach (var filter in Model.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (!filter.sharedMesh) continue;
                    var bounds = filter.sharedMesh.bounds;
                    var matrix = Model.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                    for (var x = -1; x <= 1; x++)
                    for (var y = -1; y <= 1; y++)
                    for (var z = -1; z <= 1; z++)
                        points.Add(matrix.MultiplyPoint3x4(bounds.center +
                            Vector3.Scale(bounds.extents, new Vector3(x, y, z))));
                }
                _carryPoints = points.ToArray();
            }
            var axis = _graphics.transform.forward * (left ? -1f : 1f);
            var target = 0f;
            if (_hand.position.y < hips.position.y + (_hand.position - upperArm.position).magnitude * 0.15f)
            {
                var radius = SegmentRadius(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg,
                    left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg,
                    left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg, 0.22f);
                var native = _hand.localToWorldMatrix * Matrix4x4.TRS(handPosition, handRotation, Vector3.one);
                var leg = knee.position - thigh.position;
                var lengthSquared = leg.sqrMagnitude;
                float Gap(float angle)
                {
                    var delta = Quaternion.AngleAxis(angle, axis);
                    var minimum = float.PositiveInfinity;
                    foreach (var point in _carryPoints)
                    {
                        var world = upperArm.position + delta * (native.MultiplyPoint3x4(point) - upperArm.position);
                        var t = lengthSquared > 0f
                            ? Mathf.Clamp01(Vector3.Dot(world - thigh.position, leg) / lengthSquared) : 0f;
                        minimum = Mathf.Min(minimum, (world - thigh.position - leg * t).magnitude - radius);
                    }
                    return minimum;
                }
                if (Gap(0f) < 0.008f)
                {
                    var best = float.NegativeInfinity;
                    for (var angle = 2f; angle <= 35f; angle += 1f)
                    {
                        var gap = Gap(angle);
                        if (gap > best) { target = angle; best = gap; }
                        if (gap >= 0.008f) break;
                    }
                }
            }
            if (!_carryAngleInitialized)
            {
                _carryAngle = target;
                _carryAngleInitialized = true;
            }
            else if (_carryFrame != Time.frameCount)
                _carryAngle = Mathf.MoveTowards(_carryAngle, target, Time.deltaTime * 90f);
            _carryFrame = Time.frameCount;
            if (_carryAngle <= 0.001f) return;
            _carryOriginalRotation = upperArm.localRotation;
            upperArm.rotation = Quaternion.AngleAxis(_carryAngle, axis) * upperArm.rotation;
            _carryAppliedRotation = upperArm.localRotation;
            _carryPoseApplied = true;
        }

        private bool MechanismCurrent()
        {
            if (!_frameFilter || !_stringFilter || _frameFilter.sharedMesh != _frameMesh ||
                _stringFilter.sharedMesh != _stringMesh || !_weaponAnimator ||
                _weaponAnimator.runtimeAnimatorController != _weaponController || !_weaponBolt ||
                !_weaponAnimator.transform.IsChildOf(transform)) return false;
            for (var i = 0; i < _weaponBones.Length; i++)
                if (!_weaponBones[i] || _weaponBones[i].parent != _weaponParents[i]) return false;
            return true;
        }

        private bool MechanismFault(string reason)
        {
            _mechanismReady = false;
            _mechanismFailed = true;
            MechanismStatus = reason;
            return false;
        }

        private bool PrepareMechanism(WieldedMechanism data)
        {
            if (_mechanismFailed) return false;
            if (!string.Equals(_item.ItemDefinition.ItemPresentation?.AssetReference?.AssetGUID,
                    data.NativeGuid, StringComparison.OrdinalIgnoreCase)) return MechanismFault("NativeSourceMismatch");
            if (!_frameMesh)
            {
                var frame = Model.Find(data.FramePart);
                var cord = Model.Find(data.StringPart);
                _frameFilter = frame ? frame.GetComponent<MeshFilter>() : null;
                _stringFilter = cord ? cord.GetComponent<MeshFilter>() : null;
                if (!_frameFilter || !_stringFilter || !_frameFilter.sharedMesh || !_stringFilter.sharedMesh ||
                    !OrderedSourceMatches(data.FramePart, data.FrameHash) ||
                    !OrderedSourceMatches(data.StringPart, data.StringHash)) return MechanismFault("AuthoredSourceMismatch");
                _frameOriginal = _frameFilter.sharedMesh;
                _stringOriginal = _stringFilter.sharedMesh;
                if (_frameOriginal.vertexCount != data.FrameCount ||
                    _stringOriginal.vertexCount != data.StringIndices.Count) return MechanismFault("VertexMapMismatch");
                _frameVertices = _frameOriginal.vertices;
                _frameBaseNormals = _frameOriginal.normals;
                _frameBaseTangents = _frameOriginal.tangents;
                _frameNormals = (Vector3[])_frameBaseNormals.Clone();
                _frameTangents = (Vector4[])_frameBaseTangents.Clone();
                _stringVertices = _stringOriginal.vertices;
                _stringBaseNormals = _stringOriginal.normals;
                _stringBaseTangents = _stringOriginal.tangents;
                _stringNormals = (Vector3[])_stringBaseNormals.Clone();
                _stringTangents = (Vector4[])_stringBaseTangents.Clone();
                if (_frameNormals.Length != data.FrameCount || _frameTangents.Length != data.FrameCount ||
                    _stringNormals.Length != data.StringIndices.Count || _stringTangents.Length != data.StringIndices.Count)
                    return MechanismFault("MeshChannelsMissing");
                _centers = new WieldedMechanism.Point[data.Segments + 1];
                _loadedCenters = new WieldedMechanism.Point[data.Segments + 1];
                if (!data.TryGeometry(0, data.NativeSpanLoaded, _loadedCenters, out _loadedStringNodes))
                    return MechanismFault("LoadedGeometryInvalid");
                foreach (var row in data.LimbRows)
                {
                    var center = _loadedCenters[row.Section];
                    if (row.Left) center = new WieldedMechanism.Point(-center.X, center.Y, center.Z);
                    var normal = data.RingNormal(_loadedCenters, row.Section, row.Left);
                    var corner = data.Corners[row.Corner];
                    var width = data.WidthRoot + (data.WidthTip - data.WidthRoot) * row.Section / data.Segments;
                    var point = (center + new WieldedMechanism.Point(0, corner.Height, 0) +
                        normal * (width * corner.Sign)).Float;
                    if ((_frameVertices[row.Index] - point).sqrMagnitude > 0.00000001f)
                        return MechanismFault("LoadedVertexMapMismatch");
                }
                foreach (var row in data.RigidRows)
                    if ((_frameVertices[row.Index] - row.Position.Float).sqrMagnitude > 0.00000001f)
                        return MechanismFault("LoadedVertexMapMismatch");
                for (var i = 0; i < data.StringIndices.Count; i++)
                {
                    var row = data.StringRows[data.StringIndices[i]];
                    var basis = WieldedMechanism.StringFrame(_loadedStringNodes, row.Ring);
                    var point = (_loadedStringNodes[row.Ring] + basis.Tangent * row.Offset.X +
                        basis.Up * row.Offset.Y + basis.Side * row.Offset.Z).Float;
                    if ((_stringVertices[i] - point).sqrMagnitude > 0.00000001f)
                        return MechanismFault("LoadedStringMapMismatch");
                }
                _mechanismBolts = data.BoltParts.Select(name => Model.Find(name)).ToArray();
                if (_mechanismBolts.Any(part => !part)) return MechanismFault("BoltPartMissing");
                if (data.BoltHash != null && data.BoltParts.Any(name => !ModelSourceMatches(name, false, data.BoltHash)))
                    return MechanismFault("BoltSourceMismatch");
                _frameMesh = Instantiate(_frameOriginal);
                _stringMesh = Instantiate(_stringOriginal);
                _frameMesh.name = _frameOriginal.name + " Item Mechanism";
                _stringMesh.name = _stringOriginal.name + " Item Mechanism";
                _frameFilter.sharedMesh = _frameMesh;
                _stringFilter.sharedMesh = _stringMesh;
            }
            if (_frameFilter.sharedMesh != _frameMesh || _stringFilter.sharedMesh != _stringMesh)
                return MechanismFault("ForeignMechanismMesh");
            if (!_weaponAnimator)
            {
                _weaponBones = data.BonePaths.Select(path => transform.Find(path)).ToArray();
                _weaponBolt = transform.Find(data.BoltPath);
                if (_weaponBones.Any(bone => !bone) || !_weaponBolt)
                {
                    MechanismStatus = "PendingNativeRig";
                    return false;
                }
                var animator = GetWieldedWeaponAnimator(_graphics, _slot);
                if (animator)
                {
                    var controller = animator.runtimeAnimatorController;
                    if (controller && controller.name == data.ControllerName &&
                        _weaponBones.All(bone => bone.IsChildOf(animator.transform)))
                    {
                        var names = controller.animationClips.Select(clip => clip.name).ToArray();
                        if (data.ClipNames.All(names.Contains))
                        {
                            _weaponAnimator = animator;
                            _weaponController = controller;
                        }
                    }
                }
                if (!_weaponAnimator)
                {
                    MechanismStatus = "PendingNativeAnimator";
                    return false;
                }
                _weaponParents = _weaponBones.Select(bone => bone.parent).ToArray();
                if (_weaponBolt.parent != _weaponBones[0]) return MechanismFault("NativeBoltFrameMismatch");
            }
            if (!MechanismCurrent()) return MechanismFault("NativeRigChanged");
            if (!_weaponAnimator.enabled || !_weaponAnimator.isInitialized)
            {
                MechanismStatus = "PendingNativeAnimation";
                return false;
            }
            if (_weaponCullingLease && _weaponAnimator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                return MechanismFault("ExternalCullingWrite");
            if (!_weaponCullingLease)
            {
                _weaponOriginalCulling = _weaponAnimator.cullingMode;
                _weaponAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _weaponCullingLease = true;
            }
            return true;
        }

        private WieldedMechanism.Point ObserveSection(
            IReadOnlyList<(double X, double Y, double Z, double W)> coefficients)
        {
            var inverse = _weaponBones[0].worldToLocalMatrix;
            double x = 0, y = 0, z = 0;
            for (var i = 0; i < _weaponBones.Length; i++)
            {
                var m = inverse * _weaponBones[i].localToWorldMatrix;
                var c = coefficients[i];
                x += m.m00 * c.X + m.m01 * c.Y + m.m02 * c.Z + m.m03 * c.W;
                y += m.m10 * c.X + m.m11 * c.Y + m.m12 * c.Z + m.m13 * c.W;
                z += m.m20 * c.X + m.m21 * c.Y + m.m22 * c.Z + m.m23 * c.W;
            }
            return new WieldedMechanism.Point(x, y, z);
        }

        private static Vector3 Transport(Vector3 value, WieldedMechanism.Point before,
            WieldedMechanism.Point after)
        {
            var p = WieldedMechanism.Point.From(value);
            var up = new WieldedMechanism.Point(0, 1, 0);
            return (after * p.Dot(before) + up * p.Y + after.Cross(up) * p.Dot(before.Cross(up))).Float;
        }

        private bool UpdateMechanism()
        {
            var data = Definition?.Mechanism;
            if (data == null) return true;
            _mechanismReady = false;
            if (!Model || !PrepareMechanism(data)) return false;
            var rope = WieldedMechanism.Point.From(_weaponBones[1].localPosition);
            var q = (rope - data.RopeLoaded).Dot(data.RopeAxis) / data.RopeAxis.Dot(data.RopeAxis);
            var span = (ObserveSection(data.LeftObserver) - ObserveSection(data.RightObserver)).Length;
            if (!data.TryGeometry(q, span, _centers, out var nodes))
            {
                MechanismStatus = "UnsupportedNativeGeometry";
                return false;
            }
            foreach (var row in data.LimbRows)
            {
                var center = _centers[row.Section];
                if (row.Left) center = new WieldedMechanism.Point(-center.X, center.Y, center.Z);
                var normal = data.RingNormal(_centers, row.Section, row.Left);
                var corner = data.Corners[row.Corner];
                var width = data.WidthRoot + (data.WidthTip - data.WidthRoot) * row.Section / data.Segments;
                _frameVertices[row.Index] = (center + new WieldedMechanism.Point(0, corner.Height, 0) +
                    normal * (width * corner.Sign)).Float;
                var before = data.RingNormal(_loadedCenters, row.Section, row.Left);
                _frameNormals[row.Index] = Transport(_frameBaseNormals[row.Index], before, normal);
                var t = Transport(_frameBaseTangents[row.Index], before, normal);
                _frameTangents[row.Index] = new Vector4(t.x, t.y, t.z, _frameBaseTangents[row.Index].w);
            }
            foreach (var row in data.RigidRows)
            {
                var angle = row.Joint.Angle(q);
                _frameVertices[row.Index] = (row.Joint.Pivot + row.Joint.Rotate(row.Position - row.Joint.Pivot, angle)).Float;
                _frameNormals[row.Index] = row.Joint.Rotate(WieldedMechanism.Point.From(_frameBaseNormals[row.Index]), angle).Float;
                var t = row.Joint.Rotate(WieldedMechanism.Point.From(_frameBaseTangents[row.Index]), angle).Float;
                _frameTangents[row.Index] = new Vector4(t.x, t.y, t.z, _frameBaseTangents[row.Index].w);
            }
            for (var i = 0; i < data.StringIndices.Count; i++)
            {
                var row = data.StringRows[data.StringIndices[i]];
                var basis = WieldedMechanism.StringFrame(nodes, row.Ring);
                var loaded = WieldedMechanism.StringFrame(_loadedStringNodes, row.Ring);
                _stringVertices[i] = (nodes[row.Ring] + basis.Tangent * row.Offset.X +
                    basis.Up * row.Offset.Y + basis.Side * row.Offset.Z).Float;
                var n = WieldedMechanism.Point.From(_stringBaseNormals[i]);
                _stringNormals[i] = (basis.Tangent * n.Dot(loaded.Tangent) + basis.Up * n.Dot(loaded.Up) +
                    basis.Side * n.Dot(loaded.Side)).Float;
                var t = WieldedMechanism.Point.From(_stringBaseTangents[i]);
                var moved = (basis.Tangent * t.Dot(loaded.Tangent) + basis.Up * t.Dot(loaded.Up) +
                    basis.Side * t.Dot(loaded.Side)).Float;
                _stringTangents[i] = new Vector4(moved.x, moved.y, moved.z, _stringBaseTangents[i].w);
            }
            _frameMesh.vertices = _frameVertices;
            _frameMesh.normals = _frameNormals;
            _frameMesh.tangents = _frameTangents;
            _frameMesh.RecalculateBounds();
            _stringMesh.vertices = _stringVertices;
            _stringMesh.normals = _stringNormals;
            _stringMesh.tangents = _stringTangents;
            _stringMesh.RecalculateBounds();
            var bolt = _weaponBolt.localPosition;
            var loadedBolt = _weaponBolt.gameObject.activeInHierarchy && q >= data.BoltQMinimum && q <= data.BoltQMaximum &&
                bolt.x >= data.BoltMinimum.X && bolt.x <= data.BoltMaximum.X &&
                bolt.y >= data.BoltMinimum.Y && bolt.y <= data.BoltMaximum.Y &&
                bolt.z >= data.BoltMinimum.Z && bolt.z <= data.BoltMaximum.Z;
            foreach (var part in _mechanismBolts) part.gameObject.SetActive(loadedBolt);
            MechanismStatus = "Ready";
            _mechanismReady = true;
            return true;
        }

        private void RestoreWeaponCulling()
        {
            if (_weaponCullingLease && SameActor && _weaponAnimator &&
                _weaponAnimator.runtimeAnimatorController == _weaponController &&
                _weaponAnimator.cullingMode == AnimatorCullingMode.AlwaysAnimate)
                _weaponAnimator.cullingMode = _weaponOriginalCulling;
            _weaponCullingLease = false;
            _mechanismReady = false;
        }

        private void ReleaseMechanism()
        {
            RestoreWeaponCulling();
            if (_frameFilter && _frameFilter.sharedMesh == _frameMesh) _frameFilter.sharedMesh = _frameOriginal;
            if (_stringFilter && _stringFilter.sharedMesh == _stringMesh) _stringFilter.sharedMesh = _stringOriginal;
            if (_frameMesh) Destroy(_frameMesh);
            if (_stringMesh) Destroy(_stringMesh);
            _frameMesh = _stringMesh = null;
            _frameFilter = _stringFilter = null;
            _weaponAnimator = null;
            _weaponController = null;
            _weaponBones = _weaponParents = null;
            _weaponBolt = null;
            _mechanismBolts = null;
            _frameOriginal = _stringOriginal = null;
            _frameVertices = _frameNormals = _frameBaseNormals = null;
            _frameTangents = _frameBaseTangents = null;
            _stringVertices = _stringNormals = _stringBaseNormals = null;
            _stringTangents = _stringBaseTangents = null;
            _centers = _loadedCenters = _loadedStringNodes = null;
            _mechanismFailed = false;
            MechanismStatus = "None";
        }

        private bool ConfigureFingers(RuntimeAnimatorController source)
        {
            if (!_animator.isHuman || !_avatar || !_avatar.isValid) return false;
            var references = source.animationClips.Where(clip => clip && clip.name == "ClosedHands")
                .Distinct().Take(2).ToArray();
            if (references.Length != 1 || !references[0].isHumanMotion) return false;
            var fingers = references[0];

            var bones = HandFingerBones(_slot);
            var targets = bones.Select(_animator.GetBoneTransform).ToArray();
            if (targets.Length != 15 || targets.Any(bone => !bone))
            {
                return false;
            }
            var left = _slot == EquipmentDefinitions.SlotTypeOffHand;
            _hand = _animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            _lowerArm = _animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            if (!_hand || !_lowerArm) return false;
            _fingerBones = targets;
            _fingerOriginals = new Quaternion[targets.Length];
            _fingerApplied = new Quaternion[targets.Length];

            var root = new GameObject("UB_WieldedFingerSample") { hideFlags = HideFlags.HideAndDontSave };
            root.SetActive(false);
            var graph = default(PlayableGraph);
            try
            {
                CopyTransforms(_animator.transform, root.transform);
                var sampler = root.AddComponent<Animator>();
                sampler.avatar = _avatar;
                sampler.fireEvents = false;
                sampler.applyRootMotion = false;
                sampler.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                root.SetActive(true);
                Quaternion[] Sample(AnimationClip clip)
                {
                    if (graph.IsValid())
                    {
                        graph.Destroy();
                    }
                    graph = PlayableGraph.Create("UB Wielded Finger Reference");
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    var playable = AnimationClipPlayable.Create(graph, clip);
                    playable.SetApplyFootIK(false);
                    playable.SetApplyPlayableIK(false);
                    var output = AnimationPlayableOutput.Create(graph, "Fingers", sampler);
                    output.SetSourcePlayable(playable);
                    graph.Play();
                    playable.SetTime(clip.length * 0.5f);
                    graph.Evaluate(0);
                    return bones.Select(bone => sampler.GetBoneTransform(bone).localRotation).ToArray();
                }

                _fingerTargets = Sample(fingers);
                return true;
            }
            catch (Exception exception)
            {
                _fingerBones = null;
                Main.Error($"Unable to sample the declared equipment hand pose: {exception}");
                return false;
            }
            finally
            {
                if (graph.IsValid())
                {
                    graph.Destroy();
                }
                Destroy(root);
            }
        }

        private static void CopyTransforms(Transform source, Transform target)
        {
            target.localPosition = source.localPosition;
            target.localRotation = source.localRotation;
            target.localScale = source.localScale;
            foreach (Transform child in source)
            {
                var clone = new GameObject(child.name) { hideFlags = HideFlags.HideAndDontSave };
                clone.transform.SetParent(target, false);
                CopyTransforms(child, clone.transform);
            }
        }

        private void RestoreFingers()
        {
            if (_fingersApplied && SameActor)
            {
                for (var i = 0; i < _fingerBones.Length; i++)
                {
                    if (_fingerBones[i] && _fingerBones[i].localRotation.Equals(_fingerApplied[i]))
                    {
                        _fingerBones[i].localRotation = _fingerOriginals[i];
                    }
                }
            }
            _fingersApplied = false;
        }

        private void RestoreItemPose()
        {
            if (_itemPoseApplied && SameActor && _posedTransform)
            {
                if (_posedTransform.localPosition.Equals(_appliedPosition))
                {
                    _posedTransform.localPosition = _originalPosition;
                }
                if (_posedTransform.localRotation.Equals(_appliedRotation))
                {
                    _posedTransform.localRotation = _originalRotation;
                }
                if (_scalePoseApplied && _posedTransform.localScale.Equals(_appliedScale))
                {
                    _posedTransform.localScale = _originalScale;
                }
            }
            _itemPoseApplied = false;
            _scalePoseApplied = false;
        }

        private void SuspendPresentation()
        {
            _gripReady = false;
            RestoreCarryPose();
            RestoreWeaponCulling();
            RestoreFingers();
            RestoreItemPose();
            var changed = false;
            if (_nativeRenderers != null)
            {
                for (var i = 0; i < _nativeRenderers.Length; i++)
                {
                    if (_nativeRenderers[i] && _nativeRenderers[i].forceRenderingOff)
                    {
                        changed |= !_nativeHidden[i];
                        _nativeRenderers[i].forceRenderingOff = _nativeHidden[i];
                    }
                }
            }
            if (Model && Model.gameObject.activeSelf)
            {
                Model.gameObject.SetActive(false);
                changed = true;
            }
            if (changed && _graphics)
            {
                _graphics.RefreshRenderers();
            }
        }

        internal void Release()
        {
            SuspendPresentation();
            ReleaseMechanism();
            if (Model)
            {
                Model.gameObject.SetActive(false);
                Model.SetParent(null, false);
                Destroy(Model.gameObject);
            }

            if (_graphics)
            {
                _graphics.RefreshRenderers();
            }

            _graphics = null;
            _actor = null;
            _item = null;
            _slot = null;
            _posedTransform = null;
            _animator = null;
            _avatar = null;
            _gripDefinition = null;
            _nativeMeshFilter = null;
            _nativeOriginalMesh = null;
            _nativeKey = null;
            _gripReady = false;
            _modelNormalSign = 0f;
            _nativeGripAxisSign = 0f;
            _armSkins = null;
            _armShapeSignature = 0;
            _segmentRadii.Clear();
            _armEnvelope = null;
            _armInParent = null;
            _plateRoll = 0f;
            PlateClearanceGap = 0f;
            _carryUpperArm = null;
            _carryPoints = null;
            _carryAngle = 0f;
            _carryAngleInitialized = false;
            _carryFrame = -1;
            GripStatus = "Unbound";
            _fingerBones = null;
            _fingerTargets = null;
            _fingerOriginals = null;
            _fingerApplied = null;
            _hand = null;
            _lowerArm = null;
            _poseController = null;
            _poseSource = null;
            _poseResolutionAttempted = false;
            Model = null;
            Definition = null;
            _nativeRenderers = null;
            _nativeHidden = null;
            WieldedModels.Remove(this);
        }

        [UsedImplicitly]
        private void OnDisable() => Release();

        [UsedImplicitly]
        private void OnDestroy() => Release();
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
        private void LateUpdate() => UpdatePresentation();

        internal void UpdatePresentation()
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
    internal static AssetReference GetBlenderModel(string name) => GetBlenderModelAsset(name, false);

    // Procedural presentations address authored OBJ triplets. Keep their source
    // order in a separate prefab cache; legacy models retain mesh optimization.
    [NotNull]
    internal static AssetReference GetOrderedBlenderModel(string name) => GetBlenderModelAsset(name, true);

    private static AssetReference GetBlenderModelAsset(string name, bool preserveVertexOrder)
    {
        var prefabGuid = GetGuid(preserveVertexOrder ? name + "|authored-vertices" : name);

        if (!PrefabsByGuid.ContainsKey(prefabGuid))
        {
            BlenderModelLoader.Shared.LoadModelWithVertexOrder(name, preserveVertexOrder);
        }

        return new AssetReference(prefabGuid);
    }

    internal static AnimationClip[] GetAnimationClips(string name) => BlenderModelLoader.Shared.LoadAnimations(name);

    internal static WieldedGrip[] GetWieldedGrips(string name) =>
        BlenderModelLoader.Shared.LoadWieldedGrips(name);

    internal static WieldedModel ReadWieldedModel(JToken data)
    {
        var parts = GripArray(data, "parts").Select(value => value.Type == JTokenType.String
            ? value.Value<string>() : throw new InvalidDataException("Invalid wielded model part.")).ToArray();
        if (parts.Length == 0 || parts.Any(part => string.IsNullOrWhiteSpace(part) ||
                part != Path.GetFileName(part) || part is "." or "..") ||
            parts.Distinct(StringComparer.Ordinal).Count() != parts.Length)
            throw new InvalidDataException("Invalid wielded model parts.");
        return new WieldedModel(parts, GripVector(data, "grip"), GripVector(data, "gripAxis"),
            GripVector(data, "forwardAxis"), GripVector(data, "upAxis"), GripVector(data, "muzzle"));
    }

    private static JToken GripField(JToken data, string key) => data is JObject obj && obj[key] is { } value
        ? value : throw new InvalidDataException("Missing wielded grip field: " + key);

    private static string GripText(JToken data, string key)
    {
        var value = GripField(data, key);
        return value.Type == JTokenType.String && !string.IsNullOrEmpty(value.Value<string>())
            ? value.Value<string>() : throw new InvalidDataException("Invalid wielded grip name: " + key);
    }

    private static JArray GripArray(JToken data, string key, int count = -1)
    {
        var value = GripField(data, key);
        return value is JArray array && (count < 0 || array.Count == count)
            ? array : throw new InvalidDataException("Invalid wielded grip array: " + key);
    }

    private static float GripNumber(JToken value)
    {
        if (value == null || value.Type is not (JTokenType.Float or JTokenType.Integer))
            throw new InvalidDataException("Wielded grip number required.");
        var result = (float)value.Value<double>();
        return !float.IsNaN(result) && !float.IsInfinity(result)
            ? result : throw new InvalidDataException("Non-finite wielded grip number.");
    }

    private static int GripInteger(JToken value)
    {
        if (value == null || value.Type != JTokenType.Integer || value.Value<long>() < 0 || value.Value<long>() > int.MaxValue)
            throw new InvalidDataException("Invalid wielded grip dimension.");
        return value.Value<int>();
    }

    private static Vector3 GripVector(JToken data, string key)
    {
        var values = GripArray(data, key, 3);
        return new Vector3(GripNumber(values[0]), GripNumber(values[1]), GripNumber(values[2]));
    }

    private static bool GripRelativePath(string path) => !Path.IsPathRooted(path) && path.IndexOf('\\') < 0 &&
        path.Split('/').All(part => !string.IsNullOrEmpty(part) && part is not ("." or ".."));


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
        private readonly List<AssetBundle> _assetBundles = [];
        private readonly Dictionary<string, AnimationClip[]> _animationClips = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WieldedGrip[]> _wieldedGrips = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WieldedGrip> _wieldedGripsBySource = new(StringComparer.OrdinalIgnoreCase);
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

        public void LoadModel(string filename) => LoadModelWithVertexOrder(filename, false);

        internal void LoadModelWithVertexOrder(string filename, bool preserveVertexOrder)
        {
            // should get better performance by instantiating the prefab outside the coroutine
            var prefab = Instantiate(_template, _prefabRoot, false);
            var meshFilter = prefab.GetComponent<MeshFilter>();
            var meshRenderer = prefab.GetComponent<MeshRenderer>();

            directoryPath = Path.Combine(Path.Combine(Main.ModFolder, "BlenderModels"), filename);
            StartCoroutine(preserveVertexOrder
                ? ConstructModelWithVertexOrder(filename, prefab, meshFilter, meshRenderer, true)
                : ConstructModel(filename, prefab, meshFilter, meshRenderer));
        }

        internal AnimationClip[] LoadAnimations(string name) => LoadAssets(name, "animations", _animationClips);

        internal WieldedGrip FindWieldedGrip(string sourceGuid) =>
            !string.IsNullOrEmpty(sourceGuid) && _wieldedGripsBySource.TryGetValue(sourceGuid, out var grip)
                ? grip : null;

        private static bool SameGripSource(WieldedGrip first, WieldedGrip second) =>
            first.Slot == second.Slot && first.MeshPath == second.MeshPath && first.MeshName == second.MeshName &&
            first.VertexCount == second.VertexCount && first.SubMeshCount == second.SubMeshCount &&
            first.OriginalBounds.Equals(second.OriginalBounds) && first.GripPoint.Equals(second.GripPoint) &&
            first.GripAxis.Equals(second.GripAxis) && first.PalmNormal.Equals(second.PalmNormal) &&
            Nullable.Equals(first.ForearmSupportPoint, second.ForearmSupportPoint) &&
            (first.PlateSurface == null ? second.PlateSurface == null : first.PlateSurface.SameAs(second.PlateSurface));

        internal WieldedGrip[] LoadWieldedGrips(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (_wieldedGrips.TryGetValue(name, out var cached)) return (WieldedGrip[])cached.Clone();
            var result = new List<WieldedGrip>();
            try
            {
                if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name is "." or "..")
                    throw new InvalidDataException("Invalid wielded grip asset name.");
                var path = Path.Combine(Main.ModFolder, "BlenderModels", name, name + ".grips.json");
                using var text = File.OpenText(path);
                using var reader = new JsonTextReader(text)
                    { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double };
                var data = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read() || GripInteger(GripField(data, "schemaVersion")) != 2)
                    throw new InvalidDataException("Invalid wielded grip schema.");
                var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var asset in GripArray(data, "assets"))
                {
                    var guid = GripText(asset, "nativeSourceGuid");
                    var slot = GripText(asset, "slot");
                    var meshPath = GripText(asset, "meshPath");
                    var vertices = GripInteger(GripField(asset, "sourceVertexCount"));
                    var subMeshes = GripInteger(GripField(asset, "sourceSubMeshCount"));
                    var bounds = GripField(asset, "sourceBounds");
                    var size = GripVector(bounds, "size");
                    var axis = GripVector(asset, "gripAxis");
                    var normal = GripVector(asset, "palmNormal");
                    var forearmSupport = asset["forearmSupport"] is { Type: not JTokenType.Null } support
                        ? GripVector(support, "point") : (Vector3?)null;
                    if (!Guid.TryParseExact(guid, "N", out _) || !guids.Add(guid) ||
                        slot != EquipmentDefinitions.SlotTypeMainHand && slot != EquipmentDefinitions.SlotTypeOffHand ||
                        meshPath != "." && !GripRelativePath(meshPath) || vertices <= 0 || subMeshes <= 0 ||
                        size.x < 0f || size.y < 0f || size.z < 0f ||
                        GripText(asset, "coordinateFrame") != "NativeItemRootLocal" ||
                        axis.sqrMagnitude < 0.00000001f || normal.sqrMagnitude < 0.00000001f ||
                        float.IsNaN(axis.sqrMagnitude) || float.IsInfinity(axis.sqrMagnitude) ||
                        float.IsNaN(normal.sqrMagnitude) || float.IsInfinity(normal.sqrMagnitude) ||
                        Mathf.Abs(Vector3.Dot(axis.normalized, normal.normalized)) > 0.001f)
                        throw new InvalidDataException("Invalid native wielded grip source frame.");
                    result.Add(new WieldedGrip(slot, guid, meshPath, GripText(asset, "sourceName"), vertices,
                        subMeshes, new Bounds(GripVector(bounds, "center"), size), GripVector(asset, "gripPoint"),
                        axis, normal, forearmSupport, asset["plateBackSurface"] is { Type: not JTokenType.Null } plate
                            ? new PlateBackSurface(plate) : null));
                }
                // Validate the complete table before publishing any source.
                // Identical shared declarations retain one immutable instance;
                // conflicting geometry for the same source is never last-wins.
                for (var i = 0; i < result.Count; i++)
                {
                    if (!_wieldedGripsBySource.TryGetValue(result[i].NativeAssetGuid, out var existing)) continue;
                    if (!SameGripSource(existing, result[i]))
                        throw new InvalidDataException("Conflicting wielded grip source: " + result[i].NativeAssetGuid);
                    result[i] = existing;
                }
                foreach (var grip in result)
                {
                    _wieldedGripsBySource[grip.NativeAssetGuid] = grip;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                ArgumentException or InvalidOperationException or OverflowException or InvalidCastException or
                KeyNotFoundException)
            {
                Main.Error($"Failed to load wielded grips {name}: {ex.Message}");
                result.Clear();
            }
            var declarations = result.ToArray();
            _wieldedGrips[name] = declarations;
            return (WieldedGrip[])declarations.Clone();
        }

        private T[] LoadAssets<T>(string name, string kind, Dictionary<string, T[]> cache) where T : UnityEngine.Object
        {
            if (cache.TryGetValue(name, out var assets))
            {
                return assets;
            }

            var path = Path.Combine(Main.ModFolder, "BlenderModels", name, name + "." + kind + ".bundle");
            var bundle = File.Exists(path) ? AssetBundle.LoadFromFile(path) : null;
            if (!bundle)
            {
                Main.Error($"Failed to load {kind} bundle {path}");
                assets = [];
            }
            else
            {
                _assetBundles.Add(bundle);
                assets = bundle.LoadAllAssets<T>();
            }

            cache[name] = assets;
            return assets;
        }

        internal static void Unload()
        {
            if (!_shared)
            {
                return;
            }

            _shared.StopAllCoroutines();
            foreach (var bundle in _shared._assetBundles)
            {
                if (bundle)
                {
                    bundle.Unload(true);
                }
            }
            _shared._assetBundles.Clear();
            _shared._animationClips.Clear();
            _shared._wieldedGrips.Clear();
            _shared._wieldedGripsBySource.Clear();
            Destroy(_shared.gameObject);
            _shared = null;
        }

        private IEnumerator ConstructModel(string filename, GameObject prefab, MeshFilter meshFilter,
            MeshRenderer meshRenderer) => ConstructModelWithVertexOrder(filename, prefab, meshFilter, meshRenderer, false);

        private IEnumerator ConstructModelWithVertexOrder(
            string filename, GameObject prefab, MeshFilter meshFilter, MeshRenderer meshRenderer,
            bool preserveVertexOrder)
        {
            if (!TryReadObjectAndCorrespondingMaterialFiles(directoryPath, filename, out var obj))
            {
                yield break;
            }

            var prefabGuid = GetGuid(preserveVertexOrder ? filename + "|authored-vertices" : filename);

            meshFilter.mesh = preserveVertexOrder ? PopulateMeshWithVertexOrder(obj, true) : PopulateMesh(obj);
            meshRenderer.materials = DefineMaterial(filename, obj);
            using (var algorithm = SHA256.Create())
            using (var input = File.OpenRead(Path.Combine(directoryPath, filename + ".obj")))
            {
                PrefabObjectHashes[prefabGuid] = BitConverter.ToString(algorithm.ComputeHash(input)).Replace("-", "");
            }
            PrefabsByGuid[prefabGuid] = prefab;

            Main.Info($"Model {filename} loaded.");
        }

        private static Mesh PopulateMesh(ObjectFile obj) => PopulateMeshWithVertexOrder(obj, false);

        private static Mesh PopulateMeshWithVertexOrder(ObjectFile obj, bool preserveVertexOrder)
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
            if (!preserveVertexOrder)
            {
                mesh.Optimize();
            }

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

                _assetBundles.Add(bundle);
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
    private static readonly Dictionary<string, string> PrefabObjectHashes = [];

    private static bool OrderedSourceMatches(string part, string hash) => ModelSourceMatches(part, true, hash);

    private static bool ModelSourceMatches(string part, bool ordered, string hash) =>
        PrefabObjectHashes.TryGetValue(GetGuid(ordered ? part + "|authored-vertices" : part), out var source) &&
        string.Equals(source, hash, StringComparison.OrdinalIgnoreCase);
    private static readonly HashSet<Mesh> LoadedMeshes = [];
    private static readonly Dictionary<string, Material> MaterialsByGuid = [];
    private static readonly HashSet<Material> LoadedMaterials = [];
    private static readonly List<Texture2D> LoadedTextures = [];
    private static readonly Dictionary<string, Texture2D> TexturesByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(Mesh Mesh, int Overlays), Mesh> OverlayMeshes = [];
    private static readonly Dictionary<Mesh, Mesh> OverlayMeshSources = [];

    internal static void Unload()
    {
        foreach (var presentation in new List<WieldedModelPresentation>(WieldedModels))
        {
            if (presentation)
            {
                presentation.Release();
            }
        }
        WieldedModels.Clear();

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
        PrefabObjectHashes.Clear();

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
