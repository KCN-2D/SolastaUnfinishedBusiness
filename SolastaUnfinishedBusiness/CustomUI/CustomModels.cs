using System.Threading.Tasks;
using System.Threading;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using JetBrains.Annotations;
using Microsoft.Win32.SafeHandles;
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
                return Rotate(p, c, s);
            }
            internal Point Rotate(Point p, double c, double s)
            {
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
        internal readonly IReadOnlyList<(bool Left, int Section, double Height, double Distance)> LimbPositions;
        internal readonly IReadOnlyList<int> LimbPositionIndices;
        internal readonly IReadOnlyList<(int Index, Point Position, Joint Joint)> RigidRows;
        internal readonly IReadOnlyList<(int Ring, Point Offset)> StringRows;
        internal readonly IReadOnlyList<int> StringIndices;
        internal readonly IReadOnlyList<(double Q, double Span, double A, double B)> Seeds;
        private readonly double[] _sampleSin, _sampleSinTwice;
        private readonly double _horizontalStep, _verticalStep;

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
            _sampleSin = new double[Segments];
            _sampleSinTwice = new double[Segments];
            for (var i = 0; i < Segments; i++)
            {
                var t = (i + 0.5) / Segments;
                _sampleSin[i] = Math.Sin(Math.PI * t);
                _sampleSinTwice[i] = Math.Sin(2 * Math.PI * t);
            }
            _horizontalStep = Math.Sqrt(MaterialLength * MaterialLength - (TipY - Root.Y) * (TipY - Root.Y)) / Segments;
            _verticalStep = (TipY - Root.Y) / Segments;
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
            var positions = new List<(bool Left, int Section, double Height, double Distance)>();
            var positionIndices = new int[LimbRows.Count];
            var positionMap = new Dictionary<(bool Left, int Section, int Corner), int>();
            for (var i = 0; i < LimbRows.Count; i++)
            {
                var row = LimbRows[i];
                var key = (row.Left, row.Section, row.Corner);
                if (!positionMap.TryGetValue(key, out var position))
                {
                    position = positions.Count;
                    positionMap.Add(key, position);
                    var corner = Corners[row.Corner];
                    var width = WidthRoot + (WidthTip - WidthRoot) * row.Section / Segments;
                    positions.Add((row.Left, row.Section, corner.Height, width * corner.Sign));
                }
                positionIndices[i] = position;
            }
            LimbPositions = positions.AsReadOnly();
            LimbPositionIndices = System.Array.AsReadOnly(positionIndices);
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
            var step = _horizontalStep;
            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                var x = Root.X;
                var zz = Root.Z;
                double j00 = 0, j01 = 0, j10 = 0, j11 = 0;
                for (var i = 0; i < Segments; i++)
                {
                    var s1 = _sampleSin[i];
                    var s2 = _sampleSinTwice[i];
                    var angle = a * s1 + b * s2;
                    var sine = Math.Sin(angle);
                    var cosine = Math.Cos(angle);
                    x += step * cosine; zz += step * sine;
                    j00 -= step * sine * s1; j01 -= step * sine * s2;
                    j10 += step * cosine * s1; j11 += step * cosine * s2;
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
            var dy = _verticalStep;
            var step = _horizontalStep;
            for (var i = 0; i < Segments; i++)
            {
                var angle = a * _sampleSin[i] + b * _sampleSinTwice[i];
                result += new Point(step * Math.Cos(angle), dy, step * Math.Sin(angle));
            }
            return result;
        }
        private void FillCenters(double a, double b, Point[] centers)
        {
            centers[0] = Root;
            var dy = _verticalStep;
            var step = _horizontalStep;
            for (var i = 0; i < Segments; i++)
            {
                var angle = a * _sampleSin[i] + b * _sampleSinTwice[i];
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
        private static readonly FieldInfo OriginalBones = DeformerType?.GetField("originalBones", Fields);
        private static readonly FieldInfo RuntimeBones = DeformerType?.GetField("boneList", Fields);
        private static readonly FieldInfo SourceTargetObject = DeformerType?.BaseType?.GetField("targetObject", Fields);
        private static readonly PropertyInfo ClothStatus = ClothType?.GetProperty("Status", Fields);
        private static readonly PropertyInfo ClothInitStarted = ClothStatus?.PropertyType.GetProperty("IsInitStart", Fields);

        private static Renderer ResolveNativeRenderer(Component owner, object native)
        {
            if (!owner || native == null) return null;
            if (TargetRenderer.GetValue(owner) is Renderer declared) return declared;
            return (SourceTargetObject?.GetValue(native) as GameObject)?.GetComponent<Renderer>();
        }
        private static SkinnedMeshRenderer ResolveTarget(Component owner, object native) =>
            ResolveNativeRenderer(owner, native) as SkinnedMeshRenderer;

        private readonly SkinnedMeshRenderer _resolvedTarget = deformer == null ? renderer : ResolveTarget(cloth, deformer);
        private readonly GameObject _targetObject = deformer == null ? null : SourceTargetObject?.GetValue(deformer) as GameObject;

        internal Mesh Original { get; } = original;
        internal Component ClothOwner => cloth;
        internal object ClothDeformer => deformer;
        internal Mesh CurrentRuntimeMesh => deformer == null ? null : RuntimeMesh.GetValue(deformer) as Mesh;
        internal bool OwnerCurrent => deformer == null || cloth &&
            (TargetRenderer.GetValue(cloth) is Renderer declared ? declared == renderer :
                _resolvedTarget == renderer && (SourceTargetObject.GetValue(deformer) as GameObject) == _targetObject) &&
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

        internal bool MatchesBones(Transform[] expected)
        {
            if (!renderer || expected == null || !OwnerCurrent) return false;
            var actual = renderer.bones;
            if (deformer == null) return expected.SequenceEqual(actual);
            if (!AcceptsCurrentMesh()) return false;
            var nativeBones = OriginalBones?.GetValue(deformer) as Transform[];
            var runtimeBones = RuntimeBones?.GetValue(deformer) as Transform[];
            // Before native Init there is no runtime clone or added virtual bone yet.
            if (nativeBones is not { Length: > 0 } && runtimeBones is not { Length: > 0 } &&
                !CurrentRuntimeMesh && renderer.sharedMesh == Original &&
                ClothStatus?.GetValue(cloth) is { } status && ClothInitStarted?.GetValue(status) is false)
                return expected.SequenceEqual(actual);
            if (nativeBones == null || runtimeBones == null ||
                runtimeBones.Length != nativeBones.Length + 1 || runtimeBones[nativeBones.Length] != renderer.transform)
                return false;
            // Magica switches its renderer between the source binding and the same binding plus its virtual bone.
            for (var index = 0; index < nativeBones.Length; index++)
                if (runtimeBones[index] != nativeBones[index]) return false;
            return (expected.SequenceEqual(nativeBones) || expected.SequenceEqual(runtimeBones)) &&
                (renderer.sharedMesh == Original && actual.SequenceEqual(nativeBones) ||
                 renderer.sharedMesh == CurrentRuntimeMesh && actual.SequenceEqual(runtimeBones));
        }

        internal static Dictionary<int, BoneWeight> ReadSkinWeights(Mesh mesh, IEnumerable<int> vertices)
        {
            var selected = vertices.ToHashSet();
            var result = new Dictionary<int, BoneWeight>();
            var counts = mesh.GetBonesPerVertex(); var weights = mesh.GetAllBoneWeights(); var boneCount = mesh.bindposes.Length;
            if (counts.Length != mesh.vertexCount || selected.Any(index => index < 0 || index >= counts.Length))
                throw new InvalidOperationException("A native skin has incomplete vertex influence counts.");
            var offset = 0;
            for (var vertex = 0; vertex < counts.Length; vertex++)
            {
                var count = counts[vertex];
                if (offset > weights.Length - count)
                    throw new InvalidOperationException("A native skin has an incomplete influence span.");
                if (selected.Contains(vertex))
                {
                    if (count is < 1 or > 4)
                        throw new InvalidOperationException("A native skin has unsupported canonical influence support.");
                    var indices = new int[4]; var values = new float[4];
                    for (var influence = 0; influence < count; influence++)
                    {
                        var value = weights[offset + influence];
                        if (float.IsNaN(value.weight) || float.IsInfinity(value.weight) || value.weight <= 0f ||
                            value.boneIndex < 0 || value.boneIndex >= boneCount)
                            throw new InvalidOperationException("A native skin has an invalid canonical bone influence.");
                        indices[influence] = value.boneIndex; values[influence] = value.weight;
                    }
                    result.Add(vertex, new BoneWeight { boneIndex0 = indices[0], boneIndex1 = indices[1], boneIndex2 = indices[2], boneIndex3 = indices[3],
                        weight0 = values[0], weight1 = values[1], weight2 = values[2], weight3 = values[3] });
                }
                offset += count;
            }
            if (offset != weights.Length || result.Count != selected.Count)
                throw new InvalidOperationException("A native skin has unused or missing canonical influences.");
            return result;
        }

        internal static Dictionary<int, BoneWeight> EncodeSkinWeights(Mesh mesh, IReadOnlyDictionary<int, BoneWeight> intended)
        {
            if (intended.Count == 0) return [];
            var vertices = intended.Keys.OrderBy(index => index).ToArray();
            var boneCount = mesh.bindposes.Length;
            foreach (var vertex in vertices)
            {
                if (vertex < 0 || vertex >= mesh.vertexCount) throw new InvalidOperationException("A native skin declaration has an invalid vertex.");
                var source = intended[vertex];
                var ids = new[] { source.boneIndex0, source.boneIndex1, source.boneIndex2, source.boneIndex3 };
                var values = new[] { source.weight0, source.weight1, source.weight2, source.weight3 };
                var total = 0d; var support = new HashSet<int>();
                for (var slot = 0; slot < values.Length; slot++)
                {
                    var value = values[slot];
                    if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f ||
                        value > 0f && (ids[slot] < 0 || ids[slot] >= boneCount))
                        throw new InvalidOperationException("A native skin declaration has invalid positive support.");
                    if (value > 0f) support.Add(ids[slot]);
                    total += value;
                }
                if (support.Count == 0 || Math.Abs(total - 1d) > 0.00001d)
                    throw new InvalidOperationException("A native skin declaration is empty or unnormalized.");
            }
            Mesh encoding = null;
            try
            {
                // Unity's stored/readback representation is separate from the ideal
                // source calculation. One owned witness covers this entire binding.
                encoding = new Mesh { hideFlags = HideFlags.HideAndDontSave, vertices = new Vector3[vertices.Length],
                    bindposes = mesh.bindposes, boneWeights = vertices.Select(index => intended[index]).ToArray() };
                var expected = ReadSkinWeights(encoding, Enumerable.Range(0, vertices.Length));
                var result = new Dictionary<int, BoneWeight>();
                for (var index = 0; index < vertices.Length; index++)
                {
                    var source = intended[vertices[index]]; var canonical = expected[index];
                    var sourceBones = new[] { (source.boneIndex0, source.weight0), (source.boneIndex1, source.weight1),
                        (source.boneIndex2, source.weight2), (source.boneIndex3, source.weight3) }.Where(value => value.Item2 > 0f).Select(value => value.Item1).OrderBy(value => value);
                    var encodedBones = new[] { (canonical.boneIndex0, canonical.weight0), (canonical.boneIndex1, canonical.weight1),
                        (canonical.boneIndex2, canonical.weight2), (canonical.boneIndex3, canonical.weight3) }.Where(value => value.Item2 > 0f).Select(value => value.Item1).OrderBy(value => value);
                    if (!sourceBones.SequenceEqual(encodedBones))
                        throw new InvalidOperationException("A native skin encoding lost its intended positive bone support.");
                    result.Add(vertices[index], canonical);
                }
                return result;
            }
            finally { if (encoding) UnityEngine.Object.Destroy(encoding); }
        }

        internal static void VerifySkinEncoding(Mesh mesh, IReadOnlyDictionary<int, BoneWeight> intended)
        {
            if (intended.Count == 0) return;
            var expected = EncodeSkinWeights(mesh, intended);
            var actual = ReadSkinWeights(mesh, intended.Keys);
            foreach (var pair in expected)
                if (!pair.Value.Equals(actual[pair.Key]))
                    throw new InvalidOperationException("A native skin changed its intended order or canonical weight encoding.");
        }

        internal sealed class SurfaceSkinEncoding(byte[] counts, BoneWeight1[] weights, BoneWeight[] storedSource,
            IReadOnlyDictionary<int, BoneWeight> storedTarget, IReadOnlyDictionary<int, BoneWeight> expected, int bones, SurfaceVertex[] vertices)
        {
            internal readonly byte[] Counts = counts;
            internal readonly BoneWeight1[] Weights = weights;
            internal readonly BoneWeight[] StoredSource = storedSource;
            internal readonly IReadOnlyDictionary<int, BoneWeight> StoredTarget = storedTarget;
            internal readonly IReadOnlyDictionary<int, BoneWeight> Expected = expected;
            internal readonly int BoneCount = bones;
            internal readonly SurfaceVertex[] Vertices = vertices;
            internal readonly byte[] ContributionCounts = vertices.Select(vertex => (byte)(vertex.Sources?.Length ?? 0)).ToArray();
            internal readonly (int Anchor, float Weight)[] Contributions = vertices.SelectMany(vertex => vertex.Sources ?? []).ToArray();
            internal long Bytes => 320L + Counts.Length + 8L * Weights.Length + 32L * StoredSource.Length +
                128L * Expected.Count + ContributionCounts.Length + 8L * Contributions.Length;
            internal bool MatchesContributions(SurfaceVertex[] current)
            {
                if (!ReferenceEquals(Vertices, current)) return false;
                var at = 0;
                for (var index = 0; index < current.Length; index++)
                {
                    var values = current[index].Sources;
                    if ((values?.Length ?? 0) != ContributionCounts[index]) return false;
                    for (var slot = 0; slot < (values?.Length ?? 0); slot++)
                        if (!Contributions[at++].Equals(values[slot])) return false;
                }
                return at == Contributions.Length;
            }
        }

        internal static SurfaceSkinEncoding PrepareSurfaceSkinEncoding(Mesh original, Mesh target,
            SurfaceBinding binding, byte[] counts, BoneWeight1[] weights, int boneCount)
        {
            var starts = new int[counts.Length];
            for (var index = 1; index < starts.Length; index++) starts[index] = starts[index - 1] + counts[index - 1];
            if (counts.Length == 0 || starts[starts.Length - 1] + counts[counts.Length - 1] != weights.Length)
                throw new InvalidOperationException("A multi-source native skin has an incomplete source stream.");
            var intended = new Dictionary<int, BoneWeight>();
            for (var index = 0; index < binding.Vertices.Length; index++)
            {
                var vertex = binding.Vertices[index]; if (vertex.Native) continue;
                if (binding.IsForeignBoundaryVertex(index, counts.Length))
                {
                    intended.Add(index, binding.BoundaryOverrides[index].Weights);
                    continue;
                }
                var mixed = new Dictionary<int, double>();
                foreach (var contribution in vertex.Sources)
                {
                    var anchor = contribution.Anchor;
                    if (anchor < 0 || anchor >= counts.Length || starts[anchor] > weights.Length - counts[anchor])
                        throw new InvalidOperationException("A native surface contribution has an incomplete influence span.");
                    var nativeTotal = 0d;
                    for (var influence = 0; influence < counts[anchor]; influence++)
                    {
                        var value = weights[starts[anchor] + influence];
                        if (float.IsNaN(value.weight) || float.IsInfinity(value.weight) || value.weight <= 0f || value.weight > 1f ||
                            value.boneIndex < 0 || value.boneIndex >= boneCount)
                            throw new InvalidOperationException("A native surface contribution has an invalid positive influence.");
                        nativeTotal += value.weight;
                        mixed.TryGetValue(value.boneIndex, out var previous);
                        mixed[value.boneIndex] = previous + (double)value.weight * contribution.Weight;
                    }
                    if (Math.Abs(nativeTotal - 1d) > 0.00001d)
                        throw new InvalidOperationException("A native surface contribution has unnormalized influences.");
                }
                var ordered = mixed.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).ToArray();
                var total = ordered.Sum(pair => pair.Value);
                if (ordered.Length is < 1 or > 4 || double.IsNaN(total) || double.IsInfinity(total) || total <= 0d)
                    throw new InvalidOperationException("A multi-source surface exceeds its canonical positive skin support.");
                var ids = new int[4]; var values = new float[4];
                for (var slot = 0; slot < ordered.Length; slot++)
                {
                    ids[slot] = ordered[slot].Key; values[slot] = (float)(ordered[slot].Value / total);
                    if (float.IsNaN(values[slot]) || float.IsInfinity(values[slot]) || values[slot] <= 0f)
                        throw new InvalidOperationException("A multi-source surface lost positive support during canonical calculation.");
                }
                intended.Add(index, new BoneWeight { boneIndex0=ids[0],boneIndex1=ids[1],boneIndex2=ids[2],boneIndex3=ids[3],
                    weight0=values[0],weight1=values[1],weight2=values[2],weight3=values[3] });
            }
            return new SurfaceSkinEncoding(counts, weights, original.boneWeights, intended,
                EncodeSkinWeights(target, intended), boneCount, binding.Vertices);
        }

        // One transition owns every native consumer of the changed render surfaces.
        internal sealed class SurfaceVertex(int anchor, Vector3 offset, Vector3 normal, Vector3 tangent, bool native,
            (int Anchor, float Weight)[] sources = null, float tangentW = 0f, Vector2? sourceUv = null, int sourceSlot = -1,
            (int A, int B, int C)? surfaceTriangle = null, Vector3 surfaceBary = default,
            (int A, int B, int C)? geometryTriangle = null, Vector3 geometryBary = default)
        {
            internal readonly int Anchor = anchor;
            internal readonly Vector3 Offset = offset, Normal = normal, Tangent = tangent;
            internal readonly bool Native = native;
            // Absent for v1. V2 retains immutable native skin provenance separately
            // from its primary anchor's geometric surface frame.
            internal readonly (int Anchor, float Weight)[] Sources = sources == null ? null :
                ((int Anchor, float Weight)[])sources.Clone();
            internal readonly float TangentW = tangentW;
            internal readonly Vector2? SourceUv = sourceUv;
            internal readonly int SourceSlot = sourceSlot;
            internal readonly (int A, int B, int C)? SurfaceTriangle = surfaceTriangle;
            internal readonly Vector3 SurfaceBary = surfaceBary;
            internal readonly (int A, int B, int C)? GeometryTriangle = geometryTriangle;
            internal readonly Vector3 GeometryBary = geometryBary;
            internal const long EstimatedBytes = 176L;
            internal long EstimatedMemory => EstimatedBytes + (Sources == null ? 0L : 32L + 8L * Sources.Length);
        }
        internal sealed class SurfaceBinding
        {
            internal readonly string Fingerprint;
            internal readonly Matrix4x4 RendererToPrefab;
            internal readonly Vector4[] SourceTangents;
            internal readonly SurfaceVertex[] Vertices;
            internal readonly long VertexBytes;
            internal readonly SurfaceBinding SkinEncodingSource;
            internal SurfaceSkinEncoding PreparedSkin;
            internal readonly SurfaceBinding SurfacePlanSource;
            internal SourceGraph.NativeSurfacePlan NativeSurface;
            internal SourceGraph.NativeSurfacePlan SurfacePlan => SurfacePlanSource.NativeSurface;
            internal long SkinEncodingBytes => SkinEncodingSource.PreparedSkin?.Bytes ?? 0L;
            internal readonly Mesh BoundarySource;
            internal readonly Mesh AuthoredBase;
            // Only the immutable catalog enables this proof cache. Mutable caller-created
            // bindings still validate all source channels on each use.
            internal bool CacheAuthoredBaseValidation;
            internal AuthoredBaseProof BaseProof;
            internal long AuthoredBaseProofBytes => BaseProof?.Bytes ?? 0L;
            internal sealed class AuthoredBaseProof
            {
                private readonly WeakReference<Mesh> _source;
                private readonly Mesh _base;
                private readonly SurfaceVertex[] _prefix;
                private readonly Vector4[] _tangents;
                private readonly string _sourceFingerprint;
                internal long Bytes => 192L + _sourceFingerprint.Length * 2L + _prefix.LongLength * IntPtr.Size + _tangents.LongLength * 16L;
                internal AuthoredBaseProof(Mesh source, Mesh basis, SurfaceBinding binding)
                {
                    _source = new WeakReference<Mesh>(source); _base = basis;
                    _prefix = binding.Vertices.Take(source.vertexCount).ToArray();
                    _tangents = (Vector4[])binding.SourceTangents.Clone();
                    _sourceFingerprint = RacesContext.CharacterModelCurrentFingerprint(source);
                }
                internal bool Matches(Mesh source, Mesh basis, SurfaceBinding binding)
                {
                    if (!_source.TryGetTarget(out var previous) || !previous || !ReferenceEquals(previous, source) ||
                        !ReferenceEquals(_base, basis) || _prefix.Length != source.vertexCount ||
                        binding.Vertices.Length < _prefix.Length || !binding.SourceTangents.SequenceEqual(_tangents) ||
                        _prefix.Where((vertex, index) => !ReferenceEquals(vertex, binding.Vertices[index])).Any()) return false;
                    // A native mesh reference is not a content epoch. Validate complete
                    // current content on source-graph transitions, never on frame updates.
                    if (RacesContext.CharacterModelCurrentFingerprint(source) != _sourceFingerprint)
                        throw new InvalidOperationException("A validated native source changed its complete mesh content.");
                    return true;
                }
            }

            internal readonly IReadOnlyDictionary<int, (BoneWeight Weights, Vector3 Normal, Vector4 Tangent)> BoundaryOverrides;
            // Coupled donor samples are owned by PaletteSource and their exact boundary
            // declarations; they carry no receiver-native interpolation stencil.
            internal bool IsForeignBoundaryVertex(int index, int nativeCount) => index >= nativeCount &&
                !Vertices[index].Native && Vertices[index].Sources == null && PaletteSource.HasValue &&
                PaletteSource.Value.Renderer && PaletteSource.Value.Source != null && PaletteSource.Value.Source.Original &&
                BoundaryOverrides != null && BoundaryOverrides.ContainsKey(index);
            // Optional append-only native palette. Every entry names an already owned source,
            // its actual renderer bone index, and the receiver-to-donor bind-space transform.
            internal readonly (SkinnedMeshRenderer Renderer, SkinMeshSource Source, int Bone, Matrix4x4 ToSource)[] PaletteAdditions;
            internal readonly (SkinnedMeshRenderer Renderer, SkinMeshSource Source, Matrix4x4 ToSource, int[] ToTarget)? PaletteSource;
            internal readonly string BoundaryBlendShapeName;
            internal readonly IReadOnlyDictionary<int, (Vector3 Original, Vector3 Position, float MaximumDistance)> BoundaryPositions;
            internal readonly Matrix4x4 BoundaryPositionFrame;
            internal readonly IReadOnlyDictionary<int, (Vector3 Original, Vector3 Position)> BoundaryBlendShapePositions;

            internal SurfaceBinding(string fingerprint, Matrix4x4 rendererToPrefab,
                Vector4[] sourceTangents, SurfaceVertex[] vertices, Mesh boundarySource = null,
                IReadOnlyDictionary<int, (BoneWeight Weights, Vector3 Normal, Vector4 Tangent)> boundaryOverrides = null,
                string boundaryBlendShapeName = null,
                IReadOnlyDictionary<int, (Vector3 Original, Vector3 Position, float MaximumDistance)> boundaryPositions = null,
                Matrix4x4? boundaryPositionFrame = null,
                IReadOnlyDictionary<int, (Vector3 Original, Vector3 Position)> boundaryBlendShapePositions = null,
                (SkinnedMeshRenderer Renderer, SkinMeshSource Source, int Bone, Matrix4x4 ToSource)[] paletteAdditions = null,
                (SkinnedMeshRenderer Renderer, SkinMeshSource Source, Matrix4x4 ToSource, int[] ToTarget)? paletteSource = null,
                SurfaceBinding skinEncodingSource = null, Mesh authoredBase = null)
            {
                Fingerprint = authoredBase ? fingerprint + ":base:1:" + RacesContext.CharacterModelFingerprint(authoredBase) : fingerprint;
                RendererToPrefab = rendererToPrefab;
                SourceTangents = sourceTangents;
                Vertices = vertices;
                VertexBytes = skinEncodingSource != null && ReferenceEquals(vertices, skinEncodingSource.Vertices)
                    ? skinEncodingSource.VertexBytes : vertices.Sum(vertex => vertex.EstimatedMemory);
                SkinEncodingSource = skinEncodingSource?.SkinEncodingSource ?? this;
                SurfacePlanSource = skinEncodingSource != null && ReferenceEquals(vertices, skinEncodingSource.Vertices) &&
                    ReferenceEquals(sourceTangents, skinEncodingSource.SourceTangents) && rendererToPrefab.Equals(skinEncodingSource.RendererToPrefab)
                    ? skinEncodingSource.SurfacePlanSource : this;
                BoundarySource = boundarySource;
                AuthoredBase = authoredBase;
                PaletteAdditions = paletteAdditions?.ToArray();
                PaletteSource = paletteSource.HasValue ? (paletteSource.Value.Renderer, paletteSource.Value.Source,
                    paletteSource.Value.ToSource, paletteSource.Value.ToTarget?.ToArray()) : null;
                BoundaryBlendShapeName = boundaryBlendShapeName;
                BoundaryPositionFrame = boundaryPositionFrame ?? Matrix4x4.identity;
                if (boundaryPositions is { Count: > 0 })
                    BoundaryPositions = boundaryPositions.ToDictionary(pair => pair.Key, pair => pair.Value);
                if (boundaryBlendShapePositions is { Count: > 0 })
                    BoundaryBlendShapePositions = boundaryBlendShapePositions.ToDictionary(pair => pair.Key, pair => pair.Value);
                if (boundaryOverrides == null || boundaryOverrides.Count == 0)
                {
                    if (boundarySource || boundaryBlendShapeName != null || BoundaryPositions != null || BoundaryBlendShapePositions != null)
                        throw new InvalidOperationException("A boundary source requires its explicit vertex changes.");
                    return;
                }
                if (!boundarySource) throw new InvalidOperationException("A boundary change requires its immutable surface source.");
                BoundaryOverrides = boundaryOverrides.ToDictionary(pair => pair.Key, pair => pair.Value);
                if (BoundaryPositions != null && BoundaryPositions.Keys.Any(index => !BoundaryOverrides.ContainsKey(index)))
                    throw new InvalidOperationException("A boundary position requires its surface vertex declaration.");
                if (BoundaryBlendShapePositions != null && (string.IsNullOrWhiteSpace(boundaryBlendShapeName) ||
                    BoundaryBlendShapePositions.Keys.Any(index => !BoundaryOverrides.ContainsKey(index))))
                    throw new InvalidOperationException("A boundary frame position requires its named surface vertex declaration.");
                using var stream = new MemoryStream();
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(boundaryBlendShapeName ?? "");
                    foreach (var pair in BoundaryOverrides.OrderBy(pair => pair.Key))
                    {
                        writer.Write(pair.Key);
                        var weight = pair.Value.Weights;
                        writer.Write(weight.boneIndex0); writer.Write(weight.weight0);
                        writer.Write(weight.boneIndex1); writer.Write(weight.weight1);
                        writer.Write(weight.boneIndex2); writer.Write(weight.weight2);
                        writer.Write(weight.boneIndex3); writer.Write(weight.weight3);
                        var normal = pair.Value.Normal; var tangent = pair.Value.Tangent;
                        writer.Write(normal.x); writer.Write(normal.y); writer.Write(normal.z);
                        writer.Write(tangent.x); writer.Write(tangent.y); writer.Write(tangent.z); writer.Write(tangent.w);
                    }
                    if (BoundaryPositions != null)
                    {
                        writer.Write("positions"); writer.Write(BoundaryPositions.Count);
                        for (var component = 0; component < 16; component++) writer.Write(BoundaryPositionFrame[component]);
                        foreach (var pair in BoundaryPositions.OrderBy(pair => pair.Key))
                        {
                            writer.Write(pair.Key);
                            var original = pair.Value.Original; var position = pair.Value.Position;
                            writer.Write(original.x); writer.Write(original.y); writer.Write(original.z);
                            writer.Write(position.x); writer.Write(position.y); writer.Write(position.z);
                            writer.Write(pair.Value.MaximumDistance);
                        }
                    }
                    if (BoundaryBlendShapePositions != null)
                    {
                        writer.Write("frame-positions"); writer.Write(BoundaryBlendShapePositions.Count);
                        foreach (var pair in BoundaryBlendShapePositions.OrderBy(pair => pair.Key))
                        {
                            writer.Write(pair.Key);
                            var original = pair.Value.Original; var position = pair.Value.Position;
                            writer.Write(original.x); writer.Write(original.y); writer.Write(original.z);
                            writer.Write(position.x); writer.Write(position.y); writer.Write(position.z);
                        }
                    }
                }
                using var hash = CreateAssetSha256();
                stream.Position = 0;
                Fingerprint += ":boundary:" + BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
            }
        }
        internal static SourceGraph PrepareMeshes(GraphicsCharacter graphics,
            IReadOnlyDictionary<SkinnedMeshRenderer, (SkinMeshSource Source, Mesh Mesh)> changes) =>
            PrepareMeshes(graphics, changes, null, null);
        internal static SourceGraph PrepareMeshes(GraphicsCharacter graphics,
            IReadOnlyDictionary<SkinnedMeshRenderer, (SkinMeshSource Source, Mesh Mesh)> changes,
            IReadOnlyDictionary<SkinnedMeshRenderer, SurfaceBinding> bindings,
            Func<string, Func<UnityEngine.Object[]>, UnityEngine.Object[]> retainData,
            Func<UnityEngine.Object, Func<string>, string> sourceFingerprint = null,
            Func<Mesh, Mesh, SurfaceBinding, SourceGraph.NativeSurfacePlan, SourceGraph.NativeSurfacePacket> preparedPacket = null)
        {
            return new SourceGraph(graphics, changes, bindings, retainData, sourceFingerprint, preparedPacket);
        }

        internal static GraphicsCharacter NativeSourceGraphics(object deformer)
        {
            var target = SourceTargetObject?.GetValue(deformer) as GameObject;
            return target ? target.GetComponentInParent<GraphicsCharacter>() : null;
        }

        internal sealed class SourceGraph
        {
            private static readonly Type CoreType = Type.GetType("MagicaCloth.CoreComponent, MagicaCloth", false);
            private static readonly Type VirtualType = Type.GetType("MagicaCloth.MagicaVirtualDeformer, MagicaCloth", false);
            private static readonly Type ClothBaseType = Type.GetType("MagicaCloth.BaseCloth, MagicaCloth", false);
            private static readonly Type ManagerType = Type.GetType("MagicaCloth.MagicaPhysicsManager, MagicaCloth", false);
            internal sealed class NativeSurfacePacket
            {
                internal readonly Mesh Target;
                internal readonly NativeSurfacePlan Plan;
                internal readonly string BindingIdentity;
                internal readonly (string BundleSha256, string Asset, string Fingerprint) Owner;
                internal readonly Vector3[] Normals;
                internal readonly Vector4[] Tangents;
                internal readonly byte[] Counts;
                internal readonly NativeSurfaceFinishState.FinishMorphChannel[] Morphs;
                internal readonly long Bytes;
                internal NativeSurfacePacket(Mesh target, NativeSurfacePlan plan, string bindingIdentity,
                    (string BundleSha256, string Asset, string Fingerprint) owner, Vector3[] normals,
                    Vector4[] tangents, byte[] counts, NativeSurfaceFinishState.FinishMorphChannel[] morphs, long bytes)
                { Target = target; Plan = plan; BindingIdentity = bindingIdentity; Owner = owner;
                    Normals = normals; Tangents = tangents; Counts = counts; Morphs = morphs; Bytes = bytes; }
                internal bool Matches(Mesh target, NativeSurfacePlan plan, string bindingIdentity,
                    (string BundleSha256, string Asset, string Fingerprint) owner) =>
                    ReferenceEquals(Target, target) && ReferenceEquals(Plan, plan) && BindingIdentity == bindingIdentity && Owner == owner;
                private static unsafe int Bits(float value) => *(int*)&value;
                internal bool SameSchema(Mesh source)
                {
                    if (source.blendShapeCount != Morphs.Length) return false;
                    for (var channel = 0; channel < Morphs.Length; channel++)
                    {
                        var frames = Morphs[channel].Frames;
                        if (source.GetBlendShapeFrameCount(channel) != frames.Length) return false;
                        for (var frame = 0; frame < frames.Length; frame++)
                            if (Bits(source.GetBlendShapeFrameWeight(channel, frame)) != Bits(frames[frame].Weight)) return false;
                    }
                    return true;
                }
            }

            internal sealed class NativeSurfaceFinishState : IDisposable
            {
                private const long Limit = 32L * 1024 * 1024;
                private readonly NativeSurfacePlan _plan;
                private readonly object _deformer;
                private readonly SkinnedMeshRenderer _renderer;
                private readonly Mesh _sharedMesh, _mesh;
                private readonly Transform[] _bones;
                private readonly Matrix4x4[] _bindposes, _boneMatrices;
                private readonly Matrix4x4 _sourceInverse, _sourceNormal, _sourceTranspose;
                private readonly byte[] _boneCounts;
                private readonly int[] _boneStarts;
                private readonly Vector3[] _restNormals, _finalPositions, _finalNormals, _finalTangents;
                private readonly Vector4[] _restTangents;
                private readonly Vector3[] _fallbackNormals, _morphPositions, _morphNormals, _morphTangents;
                private readonly Vector3[] _finePositions;
                private readonly Vector4[] _fallbackTangents;
                private readonly FinishMorphChannel[] _morphs;
                private readonly float[] _lastMorphWeights, _currentMorphWeights;
                private bool _morphValid;
                private FinishNativeArray _positions, _normals, _tangents, _weights;
                private object _meshData;
                private int _finishedBuffer = -1;
                private int _finishedMode;
                private bool _inFinish;
                internal long Bytes { get; private set; }
                private GpuSurface _gpu;
                private int _cpuFineBuffers;
                private bool _gpuFinish;
                private bool _disposed;

                internal bool IsCurrent => !_disposed && _renderer && _sharedMesh && _mesh &&
                    _renderer.sharedMesh == _mesh &&
                    ReferenceEquals(Get(_deformer, "sharedMesh"), _sharedMesh) &&
                    ReferenceEquals(Get(_deformer, "mesh"), _mesh);

                public void Dispose()
                {
                    if (_disposed) return;
                    _disposed = true;
                    _gpu?.Dispose();
                    _gpu = null;
                }

                private readonly int _fineSliceCount;
                private readonly Action<int> _fineSliceAction;
                private readonly ParallelOptions _fineParallelOptions;

                internal NativeSurfaceFinishState(NativeSurfacePlan plan, object deformer,
                    SkinnedMeshRenderer renderer, Func<Mesh, NativeSurfacePlan, NativeSurfacePacket> preparedPacket = null)
                {
                    _plan = plan ?? throw new ArgumentNullException(nameof(plan));
                    var sourceDeterminant = Vector3.Dot(Vector3.Cross((Vector3)plan.SourceFrame.GetColumn(0),
                        (Vector3)plan.SourceFrame.GetColumn(1)), (Vector3)plan.SourceFrame.GetColumn(2));
                    if (!FinishFinite(sourceDeterminant) || sourceDeterminant == 0f)
                        throw new InvalidOperationException("A final surface source frame is singular.");
                    _sourceInverse = plan.SourceFrame.inverse; _sourceNormal = _sourceInverse.transpose;
                    _sourceTranspose = plan.SourceFrame.transpose;
                    _deformer = deformer ?? throw new ArgumentNullException(nameof(deformer));
                    _renderer = renderer ? renderer : throw new ArgumentNullException(nameof(renderer));
                    _sharedMesh = Get(deformer, "sharedMesh") as Mesh;
                    _mesh = Get(deformer, "mesh") as Mesh;
                    if (!_sharedMesh || !_mesh || _sharedMesh.vertexCount != plan.TargetCount ||
                        _mesh.vertexCount != plan.TargetCount || plan.NativeCount <= 0 ||
                        plan.NativeCount > plan.TargetCount)
                        throw new InvalidOperationException("A final surface owner has incompatible native meshes.");
                    var samples = checked(plan.NativeCount + plan.Points.Length);
                    var morphCount = _sharedMesh.blendShapeCount;
                    long bytes = plan.Bytes + 1216L + (long)plan.TargetCount * 61L +
                        (long)plan.NativeCount * 36L + (long)plan.Points.Length * 12L +
                        (morphCount == 0 ? 0L : (long)samples * 36L + (long)morphCount * 80L);
                    var captureBytes = (long)plan.Points.Length * 32L +
                        (morphCount == 0 ? 0L : (long)plan.TargetCount * 36L + (long)samples * sizeof(int) + 32L);
                    if (bytes + captureBytes > Limit) throw new NativeDataPacketLimit();
                    _bones = (Transform[])Get(deformer, "boneList");
                    _bindposes = _mesh.bindposes;
                    if (_bones.Length == 0 || _bones.Length != _bindposes.Length)
                        throw new InvalidOperationException("A final surface owner has an incomplete skin palette.");
                    _boneMatrices = new Matrix4x4[_bones.Length];
                    bytes = checked(bytes + _bones.LongLength * 144L);
                    if (bytes + captureBytes > Limit) throw new NativeDataPacketLimit();
                    using (var counts = _sharedMesh.GetBonesPerVertex()) _boneCounts = counts.ToArray();
                    if (_boneCounts.Length != plan.TargetCount)
                        throw new InvalidOperationException("A final surface owner has incomplete skin counts.");
                    _boneStarts = new int[plan.TargetCount + 1];
                    for (var index = 0; index < plan.TargetCount; index++)
                    {
                        if (_boneCounts[index] == 0)
                            throw new InvalidOperationException("A final surface vertex has no skin influences.");
                        _boneStarts[index + 1] = checked(_boneStarts[index] + _boneCounts[index]);
                    }
                    var packet = preparedPacket?.Invoke(_sharedMesh, plan);
                    if (packet != null && (!ReferenceEquals(packet.Target, _sharedMesh) || !ReferenceEquals(packet.Plan, plan) ||
                        !packet.Counts.SequenceEqual(_boneCounts) || !packet.SameSchema(_sharedMesh))) packet = null;
                    _restNormals = packet?.Normals ?? _sharedMesh.normals;
                    _restTangents = packet?.Tangents ?? _sharedMesh.tangents;
                    if (_restNormals.Length != plan.TargetCount || _restTangents.Length != plan.TargetCount)
                        throw new InvalidOperationException("A final surface owner has incomplete normal/tangent channels.");
                    _finalPositions = new Vector3[plan.NativeCount];
                    _finalNormals = new Vector3[plan.NativeCount];
                    _finalTangents = new Vector3[plan.NativeCount];
                    _fallbackNormals = (Vector3[])_restNormals.Clone();
                    _fallbackTangents = (Vector4[])_restTangents.Clone();
                    _finePositions = new Vector3[plan.Points.Length];
                    _fineSliceCount = Math.Min(Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2)),
                        Math.Max(1, 1 + (plan.Points.Length - 1) / 8192));
                    _fineSliceAction = FinishFineSlice;
                    _fineParallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _fineSliceCount };
                    _morphPositions = morphCount == 0 ? Array.Empty<Vector3>() : new Vector3[samples];
                    _morphNormals = morphCount == 0 ? Array.Empty<Vector3>() : new Vector3[samples];
                    _morphTangents = morphCount == 0 ? Array.Empty<Vector3>() : new Vector3[samples];
                    _lastMorphWeights = new float[morphCount]; _currentMorphWeights = new float[morphCount];
                    _morphs = packet?.Morphs ?? new FinishMorphChannel[morphCount];
                    if (_morphs.Length != _mesh.blendShapeCount)
                        throw new InvalidOperationException("A final surface owner changed its physical channels.");
                    if (packet == null)
                    {
                    if (_morphs.Length != 0)
                    {
                        var p = new Vector3[plan.TargetCount]; var n = new Vector3[p.Length]; var t = new Vector3[p.Length];
                        var selectedSamples = new int[samples];
                        for (var channel = 0; channel < _morphs.Length; channel++)
                        {
                            var frames = new FinishMorphFrame[_sharedMesh.GetBlendShapeFrameCount(channel)];
                            if (frames.Length == 0) throw new InvalidOperationException("A final surface morph has no frames.");
                            for (var frame = 0; frame < frames.Length; frame++)
                            {
                                var weight = _sharedMesh.GetBlendShapeFrameWeight(channel, frame);
                                if (!FinishFinite(weight) || (frame != 0 && weight <= frames[frame - 1].Weight) ||
                                    weight != _mesh.GetBlendShapeFrameWeight(channel, frame))
                                    throw new InvalidOperationException("A final surface morph has an invalid frame range.");
                                _sharedMesh.GetBlendShapeFrameVertices(channel, frame, p, n, t);
                                var count = 0;
                                for (var sample = 0; sample < samples; sample++)
                                {
                                    var vertex = SampleVertex(sample);
                                    if (!FinishFinite(p[vertex]) || !FinishFinite(n[vertex]) || !FinishFinite(t[vertex]))
                                        throw new InvalidOperationException("A final surface morph is not finite.");
                                    if (Nonzero(p[vertex]) || Nonzero(n[vertex]) || Nonzero(t[vertex])) selectedSamples[count++] = sample;
                                }
                                bytes = checked(bytes + count * 40L + 160L);
                                if (bytes + captureBytes > Limit) throw new NativeDataPacketLimit();
                                var indices = new int[count]; var sp = new Vector3[count];
                                var sn = new Vector3[count]; var st = new Vector3[count];
                                for (var used = 0; used < count; used++)
                                {
                                    var sample = selectedSamples[used]; var vertex = SampleVertex(sample);
                                    indices[used] = sample; sp[used] = p[vertex]; sn[used] = n[vertex]; st[used] = t[vertex];
                                }
                                frames[frame] = new FinishMorphFrame(weight, indices, sp, sn, st);
                            }
                            _morphs[channel] = new FinishMorphChannel(channel, frames);
                        }
                    }
                    }
                    else
                    {
                        foreach (var channel in _morphs)
                        {
                            var frames = channel.Frames;
                            for (var frame = 0; frame < frames.Length; frame++)
                            {
                                var weight = frames[frame].Weight;
                                if (!FinishFinite(weight) || (frame != 0 && weight <= frames[frame - 1].Weight) ||
                                    weight != _mesh.GetBlendShapeFrameWeight(channel.Index, frame))
                                    throw new InvalidOperationException("A final surface morph has an invalid frame range.");
                                bytes = checked(bytes + frames[frame].Indices.LongLength * 40L + 160L);
                                if (bytes + captureBytes > Limit) throw new NativeDataPacketLimit();
                            }
                        }
                    }
                    Bytes = bytes;
                }

                internal void RefreshMaterials()
                {
                    if (!IsCurrent) { Dispose(); return; }
                    // Source installation precedes the final native material profile.
                    // Acquire the GPU buffers after that profile has been applied.
                    if (_gpu == null)
                    {
                        _gpu = GpuSurface.Create(this, Bytes);
                        Bytes = checked(Bytes + (_gpu?.Bytes ?? 0L));
                    }
                    else if (!_gpu.CanProject()) _gpu.Suspend();
                }

                private sealed class GpuSurface : IDisposable
                {
                    private static readonly int EnabledId = Shader.PropertyToID("_UBSurfaceProjection");
                    private static readonly int CountId = Shader.PropertyToID("_UBSurfacePointCount");
                    private static readonly int PointsId = Shader.PropertyToID("_UBSurfacePoints");
                    private static readonly int PositionsId = Shader.PropertyToID("_UBSurfacePositions");
                    private static readonly int NormalsId = Shader.PropertyToID("_UBSurfaceNormals");
                    private static readonly int TangentsId = Shader.PropertyToID("_UBSurfaceTangents");
                    private static readonly int InverseId = Shader.PropertyToID("_UBSurfaceSourceInverse");
                    private static readonly int TransposeId = Shader.PropertyToID("_UBSurfaceSourceTranspose");
                    private static readonly int OwnerId = Shader.PropertyToID("_UBSurfaceOwnerToObject");
                    private readonly NativeSurfaceFinishState _state;
                    private readonly List<Material> _materials = [];
                    private readonly MaterialPropertyBlock _properties = new();
                    private readonly Bounds _originalBounds;
                    private readonly Vector2[] _originalIds;
                    private readonly float _offsetExtent;
                    private readonly GpuSurfaceRenderer _view;
                    private ComputeBuffer _points, _positions, _normals, _tangents;
                    private Bounds _sourceBounds;
                    private int _rendererBone;
                    private bool _active, _disposed;
                    internal readonly Vector3[] RestPositions;
                    internal readonly BoneWeight1[] RestWeights;
                    internal readonly long Bytes;

                    private GpuSurface(NativeSurfaceFinishState state, long bytes)
                    {
                        _state = state; Bytes = bytes;
                        _originalBounds = state._renderer.localBounds;
                        _originalIds = state._mesh.uv4;
                        RestPositions = state._sharedMesh.vertices;
                        using (var weights = state._sharedMesh.GetAllBoneWeights()) RestWeights = weights.ToArray();
                        if (RestPositions.Length != state._plan.TargetCount ||
                            RestWeights.Length != state._boneStarts[state._plan.TargetCount])
                            throw new InvalidOperationException("A GPU surface has incomplete authored fallback data.");
                        var ids = new Vector2[state._plan.TargetCount];
                        for (var index = 0; index < state._plan.Points.Length; index++)
                        {
                            var point = state._plan.Points[index];
                            ids[point.Index].x = index + 1;
                            _offsetExtent = Math.Max(_offsetExtent,
                                Math.Abs(point.Offset.x) + Math.Abs(point.Offset.y) + Math.Abs(point.Offset.z));
                        }
                        try
                        {
                            _points = new ComputeBuffer(state._plan.Points.Length, 72);
                            _positions = new ComputeBuffer(state._plan.NativeCount, 12);
                            _normals = new ComputeBuffer(state._plan.NativeCount, 12);
                            _tangents = new ComputeBuffer(state._plan.NativeCount, 12);
                            _points.SetData(state._plan.Points);
                            state._mesh.uv4 = ids;
                            _view = state._renderer.gameObject.AddComponent<GpuSurfaceRenderer>();
                            _view.Surface = this;
                        }
                        catch
                        {
                            Dispose();
                            throw;
                        }
                    }

                    internal static GpuSurface Create(NativeSurfaceFinishState state, long usedBytes)
                    {
                        if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsShaderLevel < 45 ||
                            SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 ||
                            state._plan.Points.Length == 0 || state._mesh.HasVertexAttribute(VertexAttribute.TexCoord3) ||
                            Marshal.SizeOf(typeof(NativeSurfacePoint)) != 72) return null;
                        var materials = new List<Material>();
                        state._renderer.GetSharedMaterials(materials);
                        if (materials.Count == 0 || materials.Any(material => !Supports(material, state._plan.GeometryFrames))) return null;
                        var bytes = checked(2048L + state._plan.Points.LongLength * 72L +
                            state._plan.NativeCount * 36L + state._plan.TargetCount * 20L +
                            state._boneStarts[state._plan.TargetCount] * 8L);
                        if (usedBytes + bytes > Limit) return null;
                        return new GpuSurface(state, bytes);
                    }

                    private static bool Supports(Material material, bool geometryFrames) => material && material.shader &&
                        material.shader.isSupported && material.HasProperty(EnabledId) &&
                        string.Equals(material.GetTag("UBNativeSurface", false, string.Empty), "true", StringComparison.OrdinalIgnoreCase) &&
                        (!geometryFrames || string.Equals(material.GetTag("UBGeometrySurface", false, string.Empty), "true", StringComparison.OrdinalIgnoreCase));

                    internal bool CanProject()
                    {
                        if (_disposed || !_state.IsCurrent) return false;
                        _state._renderer.GetSharedMaterials(_materials);
                        return _materials.Count > 0 && _materials.All(material => Supports(material, _state._plan.GeometryFrames));
                    }

                    internal void Suspend()
                    {
                        _active = false;
                        if (_state._renderer) SetProperties(false);
                    }

                    internal void Upload(int rendererBone)
                    {
                        _rendererBone = rendererBone;
                        _positions.SetData(_state._finalPositions);
                        _normals.SetData(_state._finalNormals);
                        _tangents.SetData(_state._finalTangents);
                        var low = _state._finalPositions[0]; var high = low;
                        foreach (var position in _state._finalPositions)
                        {
                            low = Vector3.Min(low, position); high = Vector3.Max(high, position);
                        }
                        var extent = Vector3.one * _offsetExtent;
                        _sourceBounds.SetMinMax(low - extent, high + extent);
                    }

                    internal void Publish()
                    {
                        if (!CanProject()) return;
                        _active = true;
                        UpdatePresentation();
                    }

                    internal void UpdatePresentation()
                    {
                        if (!_active || _disposed) return;
                        if (!CanProject() || !_state._bones[_rendererBone]) { Suspend(); return; }
                        SetProperties(true);
                    }

                    private void SetProperties(bool enabled)
                    {
                        var renderer = _state._renderer;
                        var owner = enabled ? renderer.worldToLocalMatrix *
                            _state._bones[_rendererBone].localToWorldMatrix * _state._bindposes[_rendererBone] : Matrix4x4.identity;
                        renderer.GetPropertyBlock(_properties);
                        WriteProperties(enabled, owner);
                        renderer.SetPropertyBlock(_properties);
                        renderer.GetSharedMaterials(_materials);
                        for (var slot = 0; slot < _materials.Count; slot++)
                        {
                            renderer.GetPropertyBlock(_properties, slot);
                            // A new per-slot block would mask native renderer-wide palette values.
                            if (_properties.isEmpty) continue;
                            WriteProperties(enabled, owner);
                            renderer.SetPropertyBlock(_properties, slot);
                        }
                        if (!enabled) { renderer.localBounds = _originalBounds; return; }
                        var transform = owner * _state._sourceInverse;
                        var first = transform.MultiplyPoint3x4(_sourceBounds.min);
                        var bounds = new Bounds(first, Vector3.zero);
                        for (var corner = 1; corner < 8; corner++)
                        {
                            var point = new Vector3((corner & 1) == 0 ? _sourceBounds.min.x : _sourceBounds.max.x,
                                (corner & 2) == 0 ? _sourceBounds.min.y : _sourceBounds.max.y,
                                (corner & 4) == 0 ? _sourceBounds.min.z : _sourceBounds.max.z);
                            bounds.Encapsulate(transform.MultiplyPoint3x4(point));
                        }
                        renderer.localBounds = bounds;
                    }

                    private void WriteProperties(bool enabled, Matrix4x4 owner)
                    {
                        _properties.SetFloat(EnabledId, enabled ? 1f : 0f);
                        _properties.SetInt(CountId, _state._plan.Points.Length);
                        _properties.SetBuffer(PointsId, enabled ? _points : null);
                        _properties.SetBuffer(PositionsId, enabled ? _positions : null);
                        _properties.SetBuffer(NormalsId, enabled ? _normals : null);
                        _properties.SetBuffer(TangentsId, enabled ? _tangents : null);
                        _properties.SetMatrix(InverseId, _state._sourceInverse);
                        _properties.SetMatrix(TransposeId, _state._sourceTranspose);
                        _properties.SetMatrix(OwnerId, owner);
                    }

                    internal static void ClearClone(SkinnedMeshRenderer renderer, MaterialPropertyBlock properties)
                    {
                        if (!renderer) return;
                        renderer.GetPropertyBlock(properties); properties.SetFloat(EnabledId, 0f);
                        renderer.SetPropertyBlock(properties);
                        var slots = renderer.sharedMaterials.Length;
                        for (var slot = 0; slot < slots; slot++)
                        {
                            renderer.GetPropertyBlock(properties, slot);
                            if (properties.isEmpty) continue;
                            properties.SetFloat(EnabledId, 0f); renderer.SetPropertyBlock(properties, slot);
                        }
                    }

                    public void Dispose() => Release(true);
                    internal void ViewDestroyed() => Release(false);

                    private void Release(bool destroyView)
                    {
                        if (_disposed) return;
                        _disposed = true; _active = false;
                        if (_state._renderer) SetProperties(false);
                        if (destroyView && _view)
                        {
                            _view.Surface = null; _view.Retired = true; _view.enabled = false;
                            UnityEngine.Object.Destroy(_view);
                        }
                        if (_state._mesh) _state._mesh.uv4 = _originalIds;
                        _points?.Dispose(); _positions?.Dispose(); _normals?.Dispose(); _tangents?.Dispose();
                    }
                }

                private sealed class GpuSurfaceRenderer : MonoBehaviour
                {
                    [NonSerialized] internal GpuSurface Surface;
                    [NonSerialized] internal bool Retired;
                    private SkinnedMeshRenderer _renderer;
                    private MaterialPropertyBlock _properties;
                    private void OnEnable()
                    {
                        if (Retired) return;
                        _renderer = GetComponent<SkinnedMeshRenderer>();
                        _properties ??= new MaterialPropertyBlock();
                        if (Surface == null) GpuSurface.ClearClone(_renderer, _properties);
                    }
                    private void LateUpdate() => Surface?.UpdatePresentation();
                    private void OnWillRenderObject()
                    {
                        if (Retired) return;
                        if (Surface == null) GpuSurface.ClearClone(_renderer, _properties);
                        else Surface.UpdatePresentation();
                    }
                    private void OnDestroy()
                    {
                        var surface = Surface; Surface = null;
                        surface?.ViewDestroyed();
                    }
                }


                internal static IEnumerator PreparePacket(Mesh target, NativeSurfacePlan plan, string identity,
                    (string BundleSha256, string Asset, string Fingerprint) owner, Func<bool> current, Action<NativeSurfacePacket> receive)
                {
                    if (!current() || !target || plan == null || target.vertexCount != plan.TargetCount ||
                        plan.NativeCount <= 0 || plan.NativeCount > plan.TargetCount) yield break;
                    var samples = checked(plan.NativeCount + plan.Points.Length); var morphCount = target.blendShapeCount;
                    long bytes = plan.Bytes + 1216L + (long)plan.TargetCount * 61L +
                        (long)plan.NativeCount * 36L + (long)plan.Points.Length * 12L +
                        (morphCount == 0 ? 0L : (long)samples * 36L + (long)morphCount * 80L);
                    var captureBytes = (long)plan.Points.Length * 32L +
                        (morphCount == 0 ? 0L : (long)plan.TargetCount * 36L + (long)samples * sizeof(int) + 32L);
                    var sliceStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    var sliceTicks = Math.Max(1L, System.Diagnostics.Stopwatch.Frequency * 3L / 1000L);
                    // Native owner bones are not available yet. The ctor retains its
                    // complete original logical cap with the actual live bone count.
                    if (bytes + captureBytes > Limit) yield break;
                    Vector3[] normals = null, p = null, n = null, t = null;
                    Vector4[] tangents = null; byte[] counts = null; int[] selected = null;
                    FinishMorphChannel[] morphs = null;
                    try
                    {
                        normals = target.normals; tangents = target.tangents;
                        using (var nativeCounts = target.GetBonesPerVertex()) counts = nativeCounts.ToArray();
                        if (normals.Length != plan.TargetCount || tangents.Length != plan.TargetCount || counts.Length != plan.TargetCount || counts.Any(value => value == 0))
                            throw new InvalidOperationException("A prepared surface packet has incomplete authored channels.");
                        morphs = new FinishMorphChannel[morphCount];
                        long retained = 512L + (long)plan.TargetCount * 29L + (long)morphCount * 128L;
                        if (retained + plan.Bytes + captureBytes > Limit) yield break;
                        if (System.Diagnostics.Stopwatch.GetTimestamp() - sliceStarted >= sliceTicks)
                        { yield return null; sliceStarted = System.Diagnostics.Stopwatch.GetTimestamp(); }
                        if (!current()) yield break;
                        if (morphCount != 0)
                        { p = new Vector3[plan.TargetCount]; n = new Vector3[p.Length]; t = new Vector3[p.Length]; selected = new int[samples]; }
                        for (var channel = 0; channel < morphCount; channel++)
                        {
                            if (!current()) yield break;
                            var frameCount = target.GetBlendShapeFrameCount(channel);
                            retained = checked(retained + (long)frameCount * 8L);
                            if (retained + plan.Bytes + captureBytes > Limit) yield break;
                            var frames = new FinishMorphFrame[frameCount];
                            if (frames.Length == 0) throw new InvalidOperationException("A final surface morph has no frames.");
                            for (var frame = 0; frame < frames.Length; frame++)
                            {
                                if (!current()) yield break;
                                var weight = target.GetBlendShapeFrameWeight(channel, frame);
                                if (!FinishFinite(weight) || (frame != 0 && weight <= frames[frame - 1].Weight))
                                    throw new InvalidOperationException("A final surface morph has an invalid frame range.");
                                target.GetBlendShapeFrameVertices(channel, frame, p, n, t);
                                var count = 0;
                                for (var sample = 0; sample < samples; sample++)
                                {
                                    var vertex = sample < plan.NativeCount ? sample : plan.Points[sample - plan.NativeCount].Index;
                                    if (!FinishFinite(p[vertex]) || !FinishFinite(n[vertex]) || !FinishFinite(t[vertex]))
                                        throw new InvalidOperationException("A final surface morph is not finite.");
                                    if (Nonzero(p[vertex]) || Nonzero(n[vertex]) || Nonzero(t[vertex])) selected[count++] = sample;
                                }
                                bytes = checked(bytes + count * 40L + 160L); retained = checked(retained + count * 40L + 384L);
                                if (bytes + captureBytes > Limit || retained + plan.Bytes + captureBytes > Limit) yield break;
                                var indices = new int[count]; var sp = new Vector3[count];
                                var sn = new Vector3[count]; var st = new Vector3[count];
                                for (var used = 0; used < count; used++)
                                {
                                    var sample = selected[used]; var vertex = sample < plan.NativeCount ? sample : plan.Points[sample - plan.NativeCount].Index;
                                    indices[used] = sample; sp[used] = p[vertex]; sn[used] = n[vertex]; st[used] = t[vertex];
                                }
                                frames[frame] = new FinishMorphFrame(weight, indices, sp, sn, st);
                                if (System.Diagnostics.Stopwatch.GetTimestamp() - sliceStarted >= sliceTicks)
                                { yield return null; sliceStarted = System.Diagnostics.Stopwatch.GetTimestamp(); }
                            }
                            morphs[channel] = new FinishMorphChannel(channel, frames);
                        }
                        if (current()) receive(new NativeSurfacePacket(target, plan, identity, owner, normals, tangents, counts, morphs, retained));
                    }
                    finally { p = n = t = normals = null; tangents = null; counts = null; selected = null; morphs = null; }
                }

                private int SampleVertex(int sample) => sample < _plan.NativeCount ? sample : _plan.Points[sample - _plan.NativeCount].Index;
                private static bool Nonzero(Vector3 value) => value.x != 0f || value.y != 0f || value.z != 0f;

                // The ready gate is the same as Finish. This must run at native Finish's existing
                // completed-job boundary; buffer 1 is the previous completed buffer in delay mode.
                internal unsafe bool BeforeFinish(object meshData, int bufferIndex)
                {
                    if (_inFinish) throw new InvalidOperationException("A final surface Finish was reentered.");
                    if (bufferIndex != 0 && bufferIndex != 1) throw new ArgumentOutOfRangeException(nameof(bufferIndex));
                    if (!IsCurrent) return false;
                    if (!FinishIsMeshUse(_deformer)) return false;
                    var meshIndex = (int)Get(_deformer, "MeshIndex");
                    var states = (IDictionary)Get(meshData, "renderMeshStateDict");
                    if (!states.Contains(meshIndex)) return false;
                    var state = states[meshIndex]; var flags = (uint)Get(state, "flag");
                    if (bufferIndex == 1 && (flags & 256u) == 0u) return false;
                    if ((flags & 1u) == 0u || (flags & 6u) != 6u) { _gpu?.Suspend(); return false; }
                    var info = FinishItem(Get(meshData, "renderMeshInfoList"), meshIndex);
                    var shared = FinishItem(Get(meshData, "sharedRenderMeshInfoList"), (int)Get(info, "renderSharedMeshIndex"));
                    var rendererBone = (int)Get(shared, "rendererBoneIndex");
                    if (rendererBone < 0 || rendererBone >= _bones.Length)
                        throw new InvalidOperationException("A final surface renderer bone is outside its palette.");
                    var vertexStart = (int)Get(state, "VertexChunkStart");
                    var weightStart = (int)Get(state, "BoneWeightChunkStart");
                    if ((int)Get(Get(info, "vertexChunk"), "dataLength") != _plan.TargetCount ||
                        (int)Get(state, "BoneWeightChunkLength") != _boneStarts[_plan.TargetCount])
                        throw new InvalidOperationException("A final surface native allocation changed its span.");
                    var mode = Convert.ToInt32(Get(_deformer, "normalAndTangentUpdateMode"));
                    if (mode < 0 || mode > 2) throw new InvalidOperationException("An unknown native normal/tangent mode was selected.");
                    if (!ReferenceEquals(_meshData, meshData))
                    {
                        _positions = new FinishNativeArray(Get(meshData, "renderPosList"), "Unity.Mathematics.float3", 12);
                        _normals = new FinishNativeArray(Get(meshData, "renderNormalList"), "Unity.Mathematics.float3", 12);
                        _tangents = new FinishNativeArray(Get(meshData, "renderTangentList"), "Unity.Mathematics.float4", 16);
                        _weights = new FinishNativeArray(Get(meshData, "renderBoneWeightList"), "UnityEngine.BoneWeight1", 8);
                        _meshData = meshData;
                    }
                    // A native allocator may replace either backing array. Borrow anew each call.
                    var positions = (Vector3*)_positions.Borrow(bufferIndex, vertexStart, _plan.TargetCount);
                    var normals = (Vector3*)_normals.Borrow(bufferIndex, vertexStart, _plan.TargetCount);
                    var tangents = (Vector4*)_tangents.Borrow(bufferIndex, vertexStart, _plan.TargetCount);
                    var weights = (FinishBoneWeight*)_weights.Borrow(bufferIndex, weightStart, _boneStarts[_plan.TargetCount]);
                    for (var bone = 0; bone < _bones.Length; bone++)
                    {
                        if (!_bones[bone]) throw new InvalidOperationException("A final surface skin bone was destroyed.");
                        _boneMatrices[bone] = _bones[bone].localToWorldMatrix * _bindposes[bone];
                    }
                    var ownerFrame = _boneMatrices[rendererBone];
                    var ownerDeterminant = Vector3.Dot(Vector3.Cross((Vector3)ownerFrame.GetColumn(0),
                        (Vector3)ownerFrame.GetColumn(1)), (Vector3)ownerFrame.GetColumn(2));
                    var ownerInverse = ownerFrame.inverse;
                    if (!FinishFinite(ownerDeterminant) || ownerDeterminant == 0f || !FinishFinite(ownerInverse.MultiplyPoint3x4(Vector3.zero)))
                        throw new InvalidOperationException("A final surface renderer frame is singular.");
                    for (var bone = 0; bone < _boneMatrices.Length; bone++)
                        _boneMatrices[bone] = ownerInverse * _boneMatrices[bone];
                    UpdateMorphs();
                    var hasMorph = _morphs.Length != 0;
                    var influenceLimit = FinishInfluenceLimit(_renderer);
                    // Resolve each native corner with the CURRENT BUFFER's actual skin weights.
                    // No native virtual-use flags are altered and no native vertex is written.
                    for (var index = 0; index < _plan.NativeCount; index++)
                    {
                        var p = positions[index] + (hasMorph ? _morphPositions[index] : Vector3.zero);
                        var n = (mode >= 1 ? normals[index] : _restNormals[index]) + (hasMorph ? _morphNormals[index] : Vector3.zero);
                        var t = (Vector3)(mode == 2 ? tangents[index] : _restTangents[index]) + (hasMorph ? _morphTangents[index] : Vector3.zero);
                        var fp = Vector3.zero; var fn = Vector3.zero; var ft = Vector3.zero;
                        var start = _boneStarts[index]; var available = _boneStarts[index + 1] - start;
                        var end = start + Math.Min(available, influenceLimit);
                        var sum = 0f; var previous = float.PositiveInfinity;
                        for (var entry = start; entry < _boneStarts[index + 1]; entry++)
                        {
                            var weight = weights[entry];
                            if (weight.Index < 0 || weight.Index >= _boneMatrices.Length || !FinishFinite(weight.Weight) ||
                                weight.Weight < 0f || weight.Weight > previous)
                                throw new InvalidOperationException("A final surface native skin weight is invalid.");
                            previous = weight.Weight;
                            if (entry < end) sum += weight.Weight;
                        }
                        if (!(sum > 0f) || !FinishFinite(sum))
                            throw new InvalidOperationException("A final surface native skin has no effective influence.");
                        // Quality selects the current buffer's leading weights, not the immutable
                        // source mesh's weights (native cloth replaces its first entry dynamically).
                        var normalization = available > influenceLimit || influenceLimit == 1 ? 1f / sum : 1f;
                        for (var entry = start; entry < end; entry++)
                        {
                            var weight = weights[entry]; var factor = weight.Weight * normalization;
                            var matrix = _boneMatrices[weight.Index];
                            fp += matrix.MultiplyPoint3x4(p) * factor;
                            fn += matrix.MultiplyVector(n) * factor;
                            ft += matrix.MultiplyVector(t) * factor;
                        }
                        // Plan residuals are measured in the immutable renderer-to-prefab frame,
                        // including its scale. Perform the interpolation in that same metric.
                        _finalPositions[index] = _plan.SourceFrame.MultiplyPoint3x4(fp);
                        _finalNormals[index] = _sourceNormal.MultiplyVector(fn).normalized;
                        _finalTangents[index] = _plan.SourceFrame.MultiplyVector(ft);
                    }
                    if (_gpu != null && _gpu.CanProject())
                    {
                        if ((_cpuFineBuffers & (1 << bufferIndex)) != 0)
                        {
                            var changed = false;
                            foreach (var point in _plan.Points)
                            {
                                positions[point.Index] = _gpu.RestPositions[point.Index];
                                normals[point.Index] = _restNormals[point.Index];
                                tangents[point.Index] = _restTangents[point.Index];
                                for (var entry = _boneStarts[point.Index]; entry < _boneStarts[point.Index + 1]; entry++)
                                {
                                    var original = _gpu.RestWeights[entry];
                                    if (weights[entry].Index == original.boneIndex && weights[entry].Weight == original.weight) continue;
                                    weights[entry].Index = original.boneIndex; weights[entry].Weight = original.weight; changed = true;
                                }
                            }
                            if (changed) Put(_deformer, "IsChangeBoneWeights", true);
                            _cpuFineBuffers &= ~(1 << bufferIndex);
                        }
                        _gpu.Upload(rendererBone);
                        _gpuFinish = true;
                        _inFinish = true; _finishedBuffer = bufferIndex; _finishedMode = mode;
                        return true;
                    }
                    _gpu?.Suspend(); _gpuFinish = false;
                    // Validate/calculations complete before publishing any fine data to native buffers.
                    // Scratch fallback arrays retain the exact original prefix in modes with no N/T upload.
                    // Only independent managed fine-point calculations run on workers.
                    // Native reads precede the join; native buffer publication follows it.
                    if (_fineSliceCount == 1)
                    {
                        FinishFineSlice(0);
                    }
                    else
                    {
                        Parallel.For(0, _fineSliceCount, _fineParallelOptions, _fineSliceAction);
                    }
                    var changedWeights = false;
                    for (var index = 0; index < _plan.Points.Length; index++)
                    {
                        var point = _plan.Points[index];
                        positions[point.Index] = _finePositions[index];
                        normals[point.Index] = _fallbackNormals[point.Index];
                        tangents[point.Index] = _fallbackTangents[point.Index];
                        for (var entry = _boneStarts[point.Index]; entry < _boneStarts[point.Index + 1]; entry++)
                        {
                            // Match native active-cloth skin output. Counts/spans stay immutable;
                            // a single renderer influence is valid for every renderer quality.
                            var factor = entry == _boneStarts[point.Index] ? 1f : 0f;
                            if (weights[entry].Index == rendererBone && weights[entry].Weight == factor) continue;
                            weights[entry].Index = rendererBone; weights[entry].Weight = factor; changedWeights = true;
                        }
                    }
                    if (changedWeights) Put(_deformer, "IsChangeBoneWeights", true);
                    _cpuFineBuffers |= 1 << bufferIndex;
                    _inFinish = true; _finishedBuffer = bufferIndex; _finishedMode = mode;
                    return true;
                }

                private void FinishFineSlice(int slice)
                {
                    var start = (int)((long)_plan.Points.Length * slice / _fineSliceCount);
                    var end = (int)((long)_plan.Points.Length * (slice + 1) / _fineSliceCount);
                    var hasMorph = _morphs.Length != 0;
                    var sourceInverse = _sourceInverse; var sourceTranspose = _sourceTranspose;
                    Vector3 framePosition = default, n = default, t = default, b = default;
                    for (var index = start; index < end; index++)
                    {
                        ref readonly var point = ref _plan.Points[index]; var bary = point.Bary;
                        // Each typed field store is the original operator-return float32 boundary.
                        Vector3 first = default, second = default, third = default, sum = default;
                        Vector3 p = default, projected = default;
                        Vector3 fineNormal = default, fineTangent = default;
                        float dot;
                        if (index == start || !_plan.ReusePreviousFrame[index])
                        {
                            var a = _finalPositions[point.A]; var c = _finalPositions[point.C]; var bb = _finalPositions[point.B];
                            first.x = a.x * bary.x; first.y = a.y * bary.x; first.z = a.z * bary.x;
                            second.x = bb.x * bary.y; second.y = bb.y * bary.y; second.z = bb.z * bary.y;
                            third.x = c.x * bary.z; third.y = c.y * bary.z; third.z = c.z * bary.z;
                            sum.x = first.x + second.x; sum.y = first.y + second.y; sum.z = first.z + second.z;
                            p.x = sum.x + third.x; p.y = sum.y + third.y; p.z = sum.z + third.z;
                            if (point.Hand == 2f)
                            {
                                t = GeometryUnit(bb - a);
                                n = GeometryUnit(Vector3.Cross(bb - a, c - a));
                                b = Vector3.Cross(n, t);
                            }
                            else
                            {
                                a = _finalNormals[point.A]; c = _finalNormals[point.C]; bb = _finalNormals[point.B];
                                first.x = a.x * bary.x; first.y = a.y * bary.x; first.z = a.z * bary.x;
                                second.x = bb.x * bary.y; second.y = bb.y * bary.y; second.z = bb.z * bary.y;
                                third.x = c.x * bary.z; third.y = c.y * bary.z; third.z = c.z * bary.z;
                                sum.x = first.x + second.x; sum.y = first.y + second.y; sum.z = first.z + second.z;
                                n.x = sum.x + third.x; n.y = sum.y + third.y; n.z = sum.z + third.z;
                                // Input fields are already binary32. Match installed Magnitude/Normalize order.
                                first.x = (float)Math.Sqrt(n.x * n.x + n.y * n.y + n.z * n.z);
                                if (first.x > 1E-05f)
                                {
                                    n.x = n.x / first.x; n.y = n.y / first.x; n.z = n.z / first.x;
                                }
                                else
                                {
                                    n.x = 0f; n.y = 0f; n.z = 0f;
                                }
                                a = _finalTangents[point.A]; c = _finalTangents[point.C]; bb = _finalTangents[point.B];
                                first.x = a.x * bary.x; first.y = a.y * bary.x; first.z = a.z * bary.x;
                                second.x = bb.x * bary.y; second.y = bb.y * bary.y; second.z = bb.z * bary.y;
                                third.x = c.x * bary.z; third.y = c.y * bary.z; third.z = c.z * bary.z;
                                sum.x = first.x + second.x; sum.y = first.y + second.y; sum.z = first.z + second.z;
                                t.x = sum.x + third.x; t.y = sum.y + third.y; t.z = sum.z + third.z;
                                first.x = n.x * t.x + n.y * t.y + n.z * t.z;
                                dot = first.x;
                                first.x = n.x * dot; first.y = n.y * dot; first.z = n.z * dot;
                                projected.x = t.x - first.x; projected.y = t.y - first.y; projected.z = t.z - first.z;
                                // Input fields are already binary32. Match installed Magnitude/Normalize order.
                                first.x = (float)Math.Sqrt(projected.x * projected.x + projected.y * projected.y + projected.z * projected.z);
                                if (first.x > 1E-05f)
                                {
                                    t.x = projected.x / first.x; t.y = projected.y / first.x; t.z = projected.z / first.x;
                                }
                                else
                                {
                                    t.x = 0f; t.y = 0f; t.z = 0f;
                                }
                                Vector3 cross = default;
                                cross.x = n.y * t.z - n.z * t.y;
                                cross.y = n.z * t.x - n.x * t.z;
                                cross.z = n.x * t.y - n.y * t.x;
                                b.x = cross.x * point.Hand; b.y = cross.y * point.Hand; b.z = cross.z * point.Hand;
                            }
                            if (!FinishFinite(p) || !FinishFinite(n) || !FinishFinite(t) || n.sqrMagnitude < .99f || t.sqrMagnitude < .99f)
                                throw new InvalidOperationException("A final surface frame is singular or not finite.");
                            framePosition = p;
                        }
                        p = framePosition;
                        var sample = _plan.NativeCount + index;
                        // Transfer preserves three product stores and both ordered sum stores.
                        var value = point.Offset;
                        first.x = t.x * value.x; first.y = t.y * value.x; first.z = t.z * value.x;
                        second.x = b.x * value.y; second.y = b.y * value.y; second.z = b.z * value.y;
                        third.x = n.x * value.z; third.y = n.y * value.z; third.z = n.z * value.z;
                        sum.x = first.x + second.x; sum.y = first.y + second.y; sum.z = first.z + second.z;
                        projected.x = sum.x + third.x; projected.y = sum.y + third.y; projected.z = sum.z + third.z;
                        p.x = p.x + projected.x; p.y = p.y + projected.y; p.z = p.z + projected.z;
                        value = point.Normal;
                        first.x = t.x * value.x; first.y = t.y * value.x; first.z = t.z * value.x;
                        second.x = b.x * value.y; second.y = b.y * value.y; second.z = b.z * value.y;
                        third.x = n.x * value.z; third.y = n.y * value.z; third.z = n.z * value.z;
                        sum.x = first.x + second.x; sum.y = first.y + second.y; sum.z = first.z + second.z;
                        fineNormal.x = sum.x + third.x; fineNormal.y = sum.y + third.y; fineNormal.z = sum.z + third.z;
                        // Input fields are already binary32. Match installed Magnitude/Normalize order.
                        first.x = (float)Math.Sqrt(fineNormal.x * fineNormal.x + fineNormal.y * fineNormal.y + fineNormal.z * fineNormal.z);
                        if (first.x > 1E-05f)
                        {
                            fineNormal.x = fineNormal.x / first.x; fineNormal.y = fineNormal.y / first.x; fineNormal.z = fineNormal.z / first.x;
                        }
                        else
                        {
                            fineNormal.x = 0f; fineNormal.y = 0f; fineNormal.z = 0f;
                        }
                        value = point.Tangent;
                        first.x = t.x * value.x; first.y = t.y * value.x; first.z = t.z * value.x;
                        second.x = b.x * value.y; second.y = b.y * value.y; second.z = b.z * value.y;
                        third.x = n.x * value.z; third.y = n.y * value.z; third.z = n.z * value.z;
                        sum.x = first.x + second.x; sum.y = first.y + second.y; sum.z = first.z + second.z;
                        fineTangent.x = sum.x + third.x; fineTangent.y = sum.y + third.y; fineTangent.z = sum.z + third.z;
                        first.x = fineNormal.x * fineTangent.x + fineNormal.y * fineTangent.y + fineNormal.z * fineTangent.z;
                        dot = first.x;
                        first.x = fineNormal.x * dot; first.y = fineNormal.y * dot; first.z = fineNormal.z * dot;
                        projected.x = fineTangent.x - first.x; projected.y = fineTangent.y - first.y; projected.z = fineTangent.z - first.z;
                        // Input fields are already binary32. Match installed Magnitude/Normalize order.
                        first.x = (float)Math.Sqrt(projected.x * projected.x + projected.y * projected.y + projected.z * projected.z);
                        if (first.x > 1E-05f)
                        {
                            fineTangent.x = projected.x / first.x; fineTangent.y = projected.y / first.x; fineTangent.z = projected.z / first.x;
                        }
                        else
                        {
                            fineTangent.x = 0f; fineTangent.y = 0f; fineTangent.z = 0f;
                        }
                        // Transport keeps original component grouping and float32 result fields.
                        projected.x = sourceInverse.m00 * p.x + sourceInverse.m01 * p.y + sourceInverse.m02 * p.z + sourceInverse.m03;
                        projected.y = sourceInverse.m10 * p.x + sourceInverse.m11 * p.y + sourceInverse.m12 * p.z + sourceInverse.m13;
                        projected.z = sourceInverse.m20 * p.x + sourceInverse.m21 * p.y + sourceInverse.m22 * p.z + sourceInverse.m23;
                        p = projected;
                        projected.x = sourceTranspose.m00 * fineNormal.x + sourceTranspose.m01 * fineNormal.y + sourceTranspose.m02 * fineNormal.z;
                        projected.y = sourceTranspose.m10 * fineNormal.x + sourceTranspose.m11 * fineNormal.y + sourceTranspose.m12 * fineNormal.z;
                        projected.z = sourceTranspose.m20 * fineNormal.x + sourceTranspose.m21 * fineNormal.y + sourceTranspose.m22 * fineNormal.z;
                        fineNormal = projected;
                        // Input fields are already binary32. Match installed Magnitude/Normalize order.
                        first.x = (float)Math.Sqrt(fineNormal.x * fineNormal.x + fineNormal.y * fineNormal.y + fineNormal.z * fineNormal.z);
                        if (first.x > 1E-05f)
                        {
                            fineNormal.x = fineNormal.x / first.x; fineNormal.y = fineNormal.y / first.x; fineNormal.z = fineNormal.z / first.x;
                        }
                        else
                        {
                            fineNormal.x = 0f; fineNormal.y = 0f; fineNormal.z = 0f;
                        }
                        projected.x = sourceInverse.m00 * fineTangent.x + sourceInverse.m01 * fineTangent.y + sourceInverse.m02 * fineTangent.z;
                        projected.y = sourceInverse.m10 * fineTangent.x + sourceInverse.m11 * fineTangent.y + sourceInverse.m12 * fineTangent.z;
                        projected.z = sourceInverse.m20 * fineTangent.x + sourceInverse.m21 * fineTangent.y + sourceInverse.m22 * fineTangent.z;
                        fineTangent = projected;
                        first.x = fineNormal.x * fineTangent.x + fineNormal.y * fineTangent.y + fineNormal.z * fineTangent.z;
                        dot = first.x;
                        first.x = fineNormal.x * dot; first.y = fineNormal.y * dot; first.z = fineNormal.z * dot;
                        projected.x = fineTangent.x - first.x; projected.y = fineTangent.y - first.y; projected.z = fineTangent.z - first.z;
                        // Input fields are already binary32. Match installed Magnitude/Normalize order.
                        first.x = (float)Math.Sqrt(projected.x * projected.x + projected.y * projected.y + projected.z * projected.z);
                        if (first.x > 1E-05f)
                        {
                            fineTangent.x = projected.x / first.x; fineTangent.y = projected.y / first.x; fineTangent.z = projected.z / first.x;
                        }
                        else
                        {
                            fineTangent.x = 0f; fineTangent.y = 0f; fineTangent.z = 0f;
                        }
                        Vector3 morphPosition = default, morphNormal = default, morphTangent = default;
                        if (hasMorph)
                        {
                            morphPosition = _morphPositions[sample]; morphNormal = _morphNormals[sample]; morphTangent = _morphTangents[sample];
                        }
                        projected.x = p.x - morphPosition.x; projected.y = p.y - morphPosition.y; projected.z = p.z - morphPosition.z;
                        _finePositions[index] = projected;
                        projected.x = fineNormal.x - morphNormal.x; projected.y = fineNormal.y - morphNormal.y; projected.z = fineNormal.z - morphNormal.z;
                        _fallbackNormals[point.Index] = projected;
                        projected.x = fineTangent.x - morphTangent.x; projected.y = fineTangent.y - morphTangent.y; projected.z = fineTangent.z - morphTangent.z;
                        Vector4 outputTangent = default;
                        outputTangent.x = projected.x; outputTangent.y = projected.y; outputTangent.z = projected.z; outputTangent.w = point.TangentHand;
                        _fallbackTangents[point.Index] = outputTangent;
                    }
                }

                // Call from postfix/finalizer with the original native exception, without swallowing it.
                // Native mode 0 restores rest N/T; mode 1 does not upload tangents. Supply only their
                // owned fine tails AFTER native Finish, retaining exact rest prefix and mode/flags.
                internal void AfterFinish(int bufferIndex, Exception nativeFailure)
                {
                    if (!_inFinish) return;
                    try
                    {
                        if (nativeFailure != null) { _gpu?.Suspend(); return; }
                        if (bufferIndex != _finishedBuffer || !_mesh || !_renderer || _renderer.sharedMesh != _mesh)
                            throw new InvalidOperationException("A final surface upload changed its native owner.");
                        if (_gpuFinish) { _gpu.Publish(); return; }
                        if (_finishedMode == 0) _mesh.normals = _fallbackNormals;
                        if (_finishedMode <= 1) _mesh.tangents = _fallbackTangents;
                    }
                    finally { _inFinish = false; _finishedBuffer = -1; _gpuFinish = false; }
                }

                private void UpdateMorphs()
                {
                    if (_morphs.Length == 0) return;
                    var changed = !_morphValid;
                    for (var index = 0; index < _morphs.Length; index++)
                    {
                        var weight = _renderer.GetBlendShapeWeight(_morphs[index].Index);
                        if (!FinishFinite(weight)) throw new InvalidOperationException("A final surface morph weight is not finite.");
                        _currentMorphWeights[index] = weight;
                        if (weight != _lastMorphWeights[index]) changed = true;
                    }
                    if (!changed) return;
                    _morphValid = false;
                    Array.Clear(_morphPositions, 0, _morphPositions.Length);
                    Array.Clear(_morphNormals, 0, _morphNormals.Length);
                    Array.Clear(_morphTangents, 0, _morphTangents.Length);
                    foreach (var channel in _morphs)
                    {
                        var weight = _currentMorphWeights[channel.Index];
                        if (weight == 0f) continue;
                        var frames = channel.Frames;
                        // Unity's neutral zero is the implicit endpoint before/after the first/last
                        // same-sign frame. Outside the last interval the endpoint interval extrapolates.
                        FinishMorphFrame first = null, second = null; float firstWeight = 0f, secondWeight = 0f;
                        if (frames[0].Weight > 0f && weight <= frames[0].Weight)
                        { second = frames[0]; secondWeight = second.Weight; }
                        else if (frames[frames.Length - 1].Weight < 0f && weight >= frames[frames.Length - 1].Weight)
                        { first = frames[frames.Length - 1]; firstWeight = first.Weight; }
                        else if (frames.Length == 1)
                        { second = frames[0]; secondWeight = second.Weight; }
                        else
                        {
                            var upper = 1;
                            while (upper < frames.Length - 1 && weight > frames[upper].Weight) upper++;
                            first = frames[upper - 1]; second = frames[upper];
                            firstWeight = first.Weight; secondWeight = second.Weight;
                        }
                        if (firstWeight == secondWeight)
                            throw new InvalidOperationException("A final surface morph has a singular interpolation interval.");
                        var factor = (weight - firstWeight) / (secondWeight - firstWeight);
                        AddMorph(first, 1f - factor); AddMorph(second, factor);
                    }
                    Array.Copy(_currentMorphWeights, _lastMorphWeights, _lastMorphWeights.Length);
                    _morphValid = true;
                }
                private void AddMorph(FinishMorphFrame frame, float factor)
                {
                    if (frame == null || factor == 0f) return;
                    for (var index = 0; index < frame.Indices.Length; index++)
                    {
                        var sample = frame.Indices[index];
                        _morphPositions[sample] += frame.Position[index] * factor;
                        _morphNormals[sample] += frame.Normal[index] * factor;
                        _morphTangents[sample] += frame.Tangent[index] * factor;
                    }
                }
                internal sealed class FinishMorphChannel(int index, FinishMorphFrame[] frames)
                { internal readonly int Index = index; internal readonly FinishMorphFrame[] Frames = frames; }
                internal sealed class FinishMorphFrame(float weight, int[] indices, Vector3[] position, Vector3[] normal, Vector3[] tangent)
                {
                    internal readonly float Weight = weight;
                    internal readonly int[] Indices = indices;
                    internal readonly Vector3[] Position = position, Normal = normal, Tangent = tangent;
                }
            }

            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            private struct FinishBoneWeight { internal float Weight; internal int Index; }

            // NativeArray layout and its pointer-load program depend only on the closed
            // value type. No backing array, borrowed address or mutable owner is retained.
            private sealed class FinishNativeArrayLayout
            {
                internal readonly string ElementName;
                internal readonly int Stride;
                internal readonly FieldInfo Length;
                internal readonly Func<object, IntPtr> ReadPointer;

                internal unsafe FinishNativeArrayLayout(Type arrayType, string elementName, int stride)
                {
                    ElementName = elementName; Stride = stride;
                    var element = arrayType.GetGenericArguments()[0];
                    if (element.FullName != elementName || Marshal.SizeOf(element) != stride ||
                        Marshal.SizeOf(typeof(Vector3)) != 12 || Marshal.SizeOf(typeof(Vector4)) != 16 ||
                        Marshal.SizeOf(typeof(FinishBoneWeight)) != 8)
                        throw new InvalidOperationException("A final surface native element has an unsupported stride.");
                    if (elementName == "UnityEngine.BoneWeight1")
                    {
                        if ((int)Marshal.OffsetOf(element, "m_Weight") != 0 || (int)Marshal.OffsetOf(element, "m_BoneIndex") != 4)
                            throw new InvalidOperationException("A final surface native bone weight layout changed.");
                    }
                    else if ((int)Marshal.OffsetOf(element, "x") != 0 || (int)Marshal.OffsetOf(element, "y") != 4 ||
                             (int)Marshal.OffsetOf(element, "z") != 8 || (stride == 16 && (int)Marshal.OffsetOf(element, "w") != 12))
                        throw new InvalidOperationException("A final surface native vector layout changed.");
                    var pointer = NativeField(arrayType, "m_Buffer");
                    Length = NativeField(arrayType, "m_Length");
                    if (pointer == null || !pointer.FieldType.IsPointer || Length?.FieldType != typeof(int))
                        throw new InvalidOperationException("A final surface native array has no borrowed buffer.");
                    // Mono reflection pointer boxing dereferences this generic struct field.
                    // Load the pointer value directly, and share only the resulting program.
                    var reader = new System.Reflection.Emit.DynamicMethod("ReadNativeSurfaceBuffer", typeof(IntPtr),
                        new[] { typeof(object) }, typeof(FinishNativeArray).Module, true);
                    var il = reader.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    il.Emit(System.Reflection.Emit.OpCodes.Unbox, arrayType);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldfld, pointer);
                    il.Emit(System.Reflection.Emit.OpCodes.Conv_I);
                    il.Emit(System.Reflection.Emit.OpCodes.Newobj, typeof(IntPtr).GetConstructor(new[] { typeof(void*) }));
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    ReadPointer = (Func<object, IntPtr>)reader.CreateDelegate(typeof(Func<object, IntPtr>));
                }
            }

            // Field metadata is cached per owner/array, never per vertex. The borrowed pointer is
            // scoped to one already-completed native Finish call and is never retained or disposed.
            private sealed class FinishNativeArray
            {
                private readonly object _chunks;
                private readonly FieldInfo _first, _second, _length;
                private readonly Func<object, IntPtr> _readPointer;
                private readonly int _stride;
                internal FinishNativeArray(object chunks, string elementName, int stride)
                {
                    _chunks = chunks; _stride = stride;
                    _first = NativeField(chunks.GetType(), "nativeArray0");
                    _second = NativeField(chunks.GetType(), "nativeArray1");
                    if (_first == null || _second == null || _first.FieldType != _second.FieldType ||
                        !_first.FieldType.IsGenericType || _first.FieldType.GetGenericTypeDefinition().FullName != "Unity.Collections.NativeArray`1")
                        throw new InvalidOperationException("A final surface native array has an unsupported layout.");
                    var layout = NativeSurfaceArrayLayout(_first.FieldType, elementName, stride);
                    _length = layout.Length;
                    _readPointer = layout.ReadPointer;
                }
                internal unsafe void* Borrow(int bufferIndex, int start, int count)
                {
                    var array = (bufferIndex == 0 ? _first : _second).GetValue(_chunks);
                    var length = (int)_length.GetValue(array);
                    if (start < 0 || count < 0 || (long)start + count > length)
                        throw new InvalidOperationException("A final surface native buffer span is outside its allocation.");
                    var pointer = _readPointer(array).ToPointer();
                    if (pointer == null) throw new InvalidOperationException("A final surface native buffer was disposed.");
                    return (byte*)pointer + checked(start * _stride);
                }
            }
            private static object FinishItem(object list, int index) =>
                list.GetType().GetProperty("Item", Fields)?.GetValue(list, new object[] { index }) ??
                throw new MissingMemberException(list.GetType().FullName, "Item");
            private static bool FinishFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
            private static bool FinishFinite(Vector3 value) => FinishFinite(value.x) && FinishFinite(value.y) && FinishFinite(value.z);
            private static Vector3 FinishTransfer(Vector3 tangent, Vector3 bitangent, Vector3 normal, Vector3 value) =>
                tangent * value.x + bitangent * value.y + normal * value.z;
            private static int FinishInfluenceLimit(SkinnedMeshRenderer renderer)
            {
                switch (renderer.quality)
                {
                    case SkinQuality.Bone1: return 1;
                    case SkinQuality.Bone2: return 2;
                    case SkinQuality.Bone4: return 4;
                    case SkinQuality.Auto:
                        var limit = (int)QualitySettings.skinWeights;
                        if (limit == 1 || limit == 2 || limit == 4 || limit == 255) return limit;
                        break;
                }
                throw new InvalidOperationException("An unknown native skin quality was selected.");
            }

            private readonly GraphicsCharacter _graphics;
            private readonly Dictionary<SkinnedMeshRenderer, (SkinMeshSource Source, Mesh Mesh)> _changes;
            private readonly List<NativeOwner> _owners = [];
            private readonly List<NativeData> _data = [];
            private readonly List<NativeSkin> _skins = [];
            private readonly Dictionary<SkinnedMeshRenderer, SkinMeshSource> _installed = [];
            private readonly Dictionary<SkinnedMeshRenderer, SurfaceBinding> _paletteBindings = [];
            private readonly Dictionary<SkinnedMeshRenderer, Dictionary<(string Name, int Occurrence), float>> _donorWeights = [];
            private readonly HashSet<Component> _boundaryVirtualOwners = [];
            private readonly HashSet<Component> _boundaryRenderOwners = [];
            private readonly Dictionary<NativeOwner, int[]> _boundaryClothVertices = [];
            private bool _applied, _started;
            private readonly Func<Mesh, Mesh, SurfaceBinding, NativeSurfacePlan, NativeSurfacePacket> _preparedPacket;
            private readonly Dictionary<SkinnedMeshRenderer, SurfaceBinding> _finishBindings;
            private readonly Dictionary<object, NativeSurfacePlan> _nativeSurfacePlans = new(new NativeDataReferences());
            private readonly Dictionary<object, NativeSurfaceFinishState> _nativeSurfaceFinishes = new(new NativeDataReferences());

            internal void RefreshNativeSurfaceMaterials()
            {
                foreach (var pair in _nativeSurfaceFinishes.ToArray())
                {
                    if (pair.Value.IsCurrent) pair.Value.RefreshMaterials();
                    else
                    {
                        pair.Value.Dispose();
                        _nativeSurfaceFinishes.Remove(pair.Key);
                    }
                }
            }

            private NativeSurfacePacket PreparedFinishPacket(SkinnedMeshRenderer renderer, Mesh target, NativeSurfacePlan plan) =>
                _preparedPacket != null && _finishBindings != null && _finishBindings.TryGetValue(renderer, out var binding) &&
                _changes.TryGetValue(renderer, out var change) && ReferenceEquals(change.Mesh, target)
                    ? _preparedPacket(change.Source.Original, target, binding, plan) : null;
            private void PrepareNativeSurfaceFinishes()
            {
                foreach (var skin in _skins)
                {
                    if (skin.Deformer == null || skin.Renderer is not SkinnedMeshRenderer renderer ||
                        !_nativeSurfacePlans.TryGetValue(skin.Deformer, out var plan) ||
                        !Flag(Get(skin.Deformer, "Status"), "IsInitSuccess")) continue;
                    if (_nativeSurfaceFinishes.TryGetValue(skin.Deformer, out var previous)) previous.Dispose();
                    _nativeSurfaceFinishes[skin.Deformer] = new NativeSurfaceFinishState(plan, skin.Deformer, renderer,
                        (target, expected) => PreparedFinishPacket(renderer, target, expected));
                }
            }
            internal bool BeginNativeSurfaceFinish(object deformer, int bufferIndex)
            {
                if (!_applied || !_nativeSurfacePlans.TryGetValue(deformer, out var plan)) return false;
                if (_nativeSurfaceFinishes.TryGetValue(deformer, out var state) && !state.IsCurrent)
                {
                    state.Dispose();
                    _nativeSurfaceFinishes.Remove(deformer);
                    state = null;
                }
                if (state == null)
                {
                    var skin = _skins.FirstOrDefault(candidate => ReferenceEquals(candidate.Deformer, deformer));
                    if (skin?.Renderer is not SkinnedMeshRenderer renderer || !renderer) return false;
                    state = new NativeSurfaceFinishState(plan, deformer, renderer,
                        (target, expected) => PreparedFinishPacket(renderer, target, expected));
                    state.RefreshMaterials();
                    _nativeSurfaceFinishes.Add(deformer, state);
                }
                return state.BeforeFinish(Get(GetStatic(ManagerType, "Instance"), "Mesh"), bufferIndex);
            }
            internal void EndNativeSurfaceFinish(object deformer, int bufferIndex, Exception failure)
            {
                if (_nativeSurfaceFinishes.TryGetValue(deformer, out var state)) state.AfterFinish(bufferIndex, failure);
            }

            private sealed class NativeOwner(Component component, object deformer, int order)
            {
                internal readonly Component Component = component;
                internal readonly object Deformer = deformer;
                internal readonly int Order = order;
                internal readonly object BoundDeformer = Optional(component, "Deformer");
                internal readonly Component[] Children = order == 1
                    ? ((IEnumerable)Get(deformer, "renderDeformerList")).Cast<Component>().ToArray() : [];
                internal readonly Component Parent = order == 0 ? Optional(component, "virtualDeformer") as Component : null;
                internal readonly Renderer Target = order == 2 ? ResolveNativeRenderer(component, deformer) : null;
                internal bool Initialized = Flag(Get(component, "Status"), "IsInitComplete");
                internal bool UserEnabled = (bool)Get(Get(component, "Status"), "userEnable");
                internal bool DeformerUserEnabled = deformer == null || (bool)Get(Get(deformer, "Status"), "userEnable");
                internal bool DeformerEnabled = deformer == null || (bool)Get(Get(deformer, "Status"), "enable");
                internal readonly int Hash = (int)Get(component, "dataHash");
                internal readonly int Version = (int)Get(component, "dataVersion");
                internal readonly int VerificationResult = Convert.ToInt32(Invoke(component, "VerifyData"));
                internal bool PreservesRejectedCloth => Order == 0 && !Initialized && VerificationResult != 0;
                internal readonly object ClothData = Optional(component, "ClothData");
                internal readonly object ClothSelection = Optional(component, "ClothSelection");
                internal readonly Dictionary<string, object> ClothDataIdentity = Optional(component, "ClothData") is { } clothData
                    ? SaveFields(clothData, "dataHash", "dataVersion") : [];
                internal readonly Dictionary<string, object> ClothSelectionIdentity = Optional(component, "ClothSelection") is { } selection
                    ? SaveFields(selection, "dataHash", "dataVersion") : [];
                internal readonly int? DeformerHash = deformer == null ? null : (int)Get(deformer, "dataHash");
                internal readonly int? DeformerVersion = deformer == null ? null : (int)Get(deformer, "dataVersion");
                internal readonly Dictionary<string, object> Verification = SaveFields(component,
                    "deformerHash", "deformerVersion", "virtualDeformerHash", "virtualDeformerVersion",
                    "clothDataHash", "clothDataVersion", "clothSelectionHash", "clothSelectionVersion",
                    "clothParamDataHashList", "meshDataHash", "meshDataVersion", "springDataHash", "springDataVersion");
                internal readonly Dictionary<string, object> DeformerVerification = deformer == null ? [] :
                    SaveFields(deformer, "renderDeformerHashList", "renderDeformerVersion");
            }
            private sealed class NativeData(object deformer, UnityEngine.Object original, UnityEngine.Object replacement)
            {
                internal readonly object Deformer = deformer;
                internal readonly UnityEngine.Object Original = original;
                internal readonly UnityEngine.Object Replacement = replacement;
                internal readonly Dictionary<string, object> OriginalIdentity = SaveFields(original, "dataHash", "dataVersion");
            }
            private sealed class NativeSkin(Renderer renderer, Mesh original, Mesh replacement, object deformer)
            {
                internal readonly Renderer Renderer = renderer;
                internal readonly Mesh Original = original;
                internal readonly Mesh Replacement = replacement;
                internal readonly object Deformer = deformer;
                internal readonly Transform[] Bones = renderer is SkinnedMeshRenderer skin ? skin.bones : [];
                internal Transform[] ReplacementBones;
                internal readonly Transform RootBone = renderer is SkinnedMeshRenderer skin ? skin.rootBone : null;
                internal bool Superseded;
                internal (string Name, int Occurrence)[] WeightChannels = PhysicalWeightChannels(original);
                internal (string Name, int Occurrence)[] OriginalWeightChannels;
                internal float[] OriginalWeights;
                internal float[] Weights = Enumerable.Range(0, original.blendShapeCount)
                    .Select(index => renderer is SkinnedMeshRenderer skin ? skin.GetBlendShapeWeight(index) : 0f).ToArray();
            }

            internal IReadOnlyDictionary<SkinnedMeshRenderer, SkinMeshSource> Installed => _installed;
            internal bool IsApplied => _applied;

            internal SourceGraph(GraphicsCharacter graphics,
                IReadOnlyDictionary<SkinnedMeshRenderer, (SkinMeshSource Source, Mesh Mesh)> changes,
                IReadOnlyDictionary<SkinnedMeshRenderer, SurfaceBinding> bindings,
                Func<string, Func<UnityEngine.Object[]>, UnityEngine.Object[]> retainData,
                Func<UnityEngine.Object, Func<string>, string> sourceFingerprint,
                Func<Mesh, Mesh, SurfaceBinding, NativeSurfacePlan, NativeSurfacePacket> preparedPacket = null)
            {
                _preparedPacket = preparedPacket;
                _graphics = graphics;
                _changes = changes.ToDictionary(pair => pair.Key, pair => pair.Value);
                foreach (var pair in _changes)
                    if (!pair.Key || !pair.Value.Mesh || pair.Value.Source == null ||
                        !pair.Value.Source.OwnerCurrent || !pair.Value.Source.AcceptsCurrentMesh())
                        throw new InvalidOperationException("A source changed before the skin transition was prepared.");
                if (_changes.Values.Any(change => change.Source.ClothOwner))
                {
                    if (CoreType == null || VirtualType == null || ClothBaseType == null || ManagerType == null)
                        throw new InvalidOperationException("The native cloth owner contract is unavailable.");
                    var all = graphics.GetComponentsInChildren(CoreType, true).Cast<Component>().ToList();
                    var selected = _changes.Values.Where(change => change.Source.ClothOwner)
                        .Select(change => change.Source.ClothOwner).ToList();
                    bool added;
                    do
                    {
                        added = false;
                        foreach (var component in all)
                        {
                            var deformer = Optional(component, "Deformer");
                            var children = VirtualType.IsInstanceOfType(component)
                                ? ((IEnumerable)Get(deformer, "renderDeformerList")).Cast<Component>().ToArray() : [];
                            var parent = ClothBaseType.IsInstanceOfType(component)
                                ? Optional(component, "virtualDeformer") as Component : null;
                            if (selected.Contains(component))
                            {
                                foreach (var linked in children.Concat(parent ? new[] { parent } : []))
                                {
                                    if (!linked || !all.Contains(linked))
                                        throw new InvalidOperationException("A native cloth relation leaves the graphics owner's hierarchy.");
                                    if (!selected.Contains(linked)) { selected.Add(linked); added = true; }
                                }
                            }
                            else if (children.Any(selected.Contains) || parent && selected.Contains(parent))
                            {
                                selected.Add(component); added = true;
                            }
                        }
                    } while (added);
                    foreach (var component in selected)
                    {
                        var deformer = Optional(component, "Deformer");
                        var order = ClothBaseType.IsInstanceOfType(component) ? 0 : VirtualType.IsInstanceOfType(component) ? 1 : 2;
                        var owner = new NativeOwner(component, order == 0 ? null : deformer, order);
                        if (Flag(Get(component, "Status"), "IsInitError") && !owner.PreservesRejectedCloth)
                            throw new InvalidOperationException("An invalid native cloth owner cannot be replaced as an ordinary skin.");
                        _owners.Add(owner);
                        if (order == 2)
                        {
                            var renderer = ResolveNativeRenderer(component, deformer);
                            if (!renderer) throw new InvalidOperationException("The native render owner has not resolved its target.");
                            var original = SourceMesh.GetValue(deformer) as Mesh;
                            if (!original) throw new InvalidOperationException("A native render owner lost its immutable source mesh.");
                            var replacement = renderer is SkinnedMeshRenderer skin && _changes.TryGetValue(skin, out var change) ? change.Mesh : original;
                            _skins.Add(new NativeSkin(renderer, original, replacement, deformer));
                        }
                    }
                }
                foreach (var pair in _changes.Where(pair => !pair.Value.Source.ClothOwner))
                    _skins.Add(new NativeSkin(pair.Key, pair.Value.Source.Original, pair.Value.Mesh, null));
                if (bindings != null && bindings.Count != 0)
                {
                    if (retainData == null)
                        throw new InvalidOperationException("A derived source binding requires its catalog owner.");
                    PrepareBindings(bindings, retainData, sourceFingerprint);
                    if (preparedPacket != null)
                        foreach (var skin in _skins)
                            if (skin.Renderer is SkinnedMeshRenderer renderer && skin.Deformer != null &&
                                _nativeSurfacePlans.TryGetValue(skin.Deformer, out var plan) &&
                                bindings.TryGetValue(renderer, out var binding) && _changes.TryGetValue(renderer, out var change) &&
                                preparedPacket(change.Source.Original, skin.Replacement, binding, plan) != null)
                            {
                                _finishBindings ??= new();
                                _finishBindings.Add(renderer, binding);
                            }
                }
                foreach (var skin in _skins.Where(skin => skin.Deformer != null && skin.Original != skin.Replacement &&
                             !_data.Any(data => ReferenceEquals(data.Deformer, skin.Deformer))))
                    if (skin.Original.vertexCount != skin.Replacement.vertexCount ||
                        skin.Original.subMeshCount != skin.Replacement.subMeshCount ||
                        !skin.Original.boneWeights.SequenceEqual(skin.Replacement.boneWeights) ||
                        Enumerable.Range(0, skin.Original.subMeshCount).Any(index =>
                            !skin.Original.GetIndices(index).SequenceEqual(skin.Replacement.GetIndices(index))))
                        throw new InvalidOperationException("A source transition changed native cloth topology or skin binding without its matching native mesh-data binding.");
                ValidateRelations();
                ValidateProspectiveData();
            }

            private void PrepareBindings(IReadOnlyDictionary<SkinnedMeshRenderer, SurfaceBinding> bindings,
                Func<string, Func<UnityEngine.Object[]>, UnityEngine.Object[]> retainData,
                Func<UnityEngine.Object, Func<string>, string> sourceFingerprint)
            {
                string Fingerprint(object source) => sourceFingerprint != null && source is UnityEngine.Object native
                    ? sourceFingerprint(native, () => HashNativeData(source)) : HashNativeData(source);
                foreach (var pair in bindings)
                {
                    if (!_changes.TryGetValue(pair.Key, out var change) || pair.Value == null ||
                        pair.Value.Vertices.Length != change.Mesh.vertexCount ||
                        pair.Value.SourceTangents.Length != change.Source.Original.vertexCount)
                        throw new InvalidOperationException("An authored source attachment does not match its render source.");
                    var skin = _skins.Single(value => value.Renderer == pair.Key);
                    skin.ReplacementBones = PreparePalette(pair.Key, change, pair.Value);
                    if (pair.Value.PaletteSource.HasValue) _paletteBindings.Add(pair.Key, pair.Value);
                    ValidateBinding(change.Source.Original, change.Mesh, pair.Value, change.Source.ClothOwner,
                        change.Source.ClothOwner ? NativeSkinBoundaryVertices(change.Source, pair.Value) : null);
                }
                foreach (var owner in _owners.Where(owner => owner.Order == 1))
                {
                    var recipe = DescribeNativeBinding(owner.Deformer, owner.Children, _changes, bindings);
                    if (recipe == null) continue;
                    var key = recipe.Key(Fingerprint);
                    var derived = retainData(key, () => recipe.Create());
                    if (derived.Length != recipe.Changed.Length + 1)
                        throw new InvalidOperationException("The retained native binding has a different child layout.");
                    _data.Add(new NativeData(owner.Deformer, recipe.Original, derived[0]));
                    for (var index = 0; index < recipe.Changed.Length; index++)
                    {
                        var row = recipe.Changed[index];
                        _data.Add(new NativeData(row.Deformer, row.Source, derived[index + 1]));
                        if (row.Binding.SurfacePlan is { Points.Length: > 0 } plan)
                            _nativeSurfacePlans.Add(row.Deformer, plan);
                    }
                }
            }
            private Transform[] PreparePalette(SkinnedMeshRenderer renderer,
                (SkinMeshSource Source, Mesh Mesh) change, SurfaceBinding binding)
            {
                var original = change.Source.Original.bindposes; var authored = change.Mesh.bindposes;
                var current = renderer.bones; var additions = binding.PaletteAdditions ?? [];
                if (binding.PaletteSource is not { } source)
                {
                    if (additions.Length != 0 || !original.SequenceEqual(authored))
                        throw new InvalidOperationException("An undeclared source palette changed.");
                    // Ordinary native/hair/cloth bindings keep their existing palette lifecycle.
                    return null;
                }
                var captured = _skins.Single(skin => skin.Renderer == renderer);
                if (change.Source.ClothOwner || current.Length != original.Length || !current.SequenceEqual(captured.Bones) ||
                    !ReferenceEquals(renderer.rootBone, captured.RootBone) || authored.Length != original.Length + additions.Length ||
                    !authored.Take(original.Length).SequenceEqual(original) ||
                    !source.Renderer || source.Source == null || source.Source.ClothOwner || !source.Source.OwnerCurrent ||
                    !source.Source.AcceptsCurrentMesh() || !source.Renderer.transform.IsChildOf(_graphics.transform))
                    throw new InvalidOperationException("A coupled source palette has an incompatible native owner or generation.");
                var donorBones = source.Renderer.bones; var donorPoses = source.Source.Original.bindposes;
                if (donorBones.Length != donorPoses.Length || source.ToTarget == null || source.ToTarget.Length != donorBones.Length)
                    throw new InvalidOperationException("A coupled donor has incomplete palette correspondence.");
                var result = current.ToList();
                foreach (var row in additions)
                {
                    if (!ReferenceEquals(row.Renderer, source.Renderer) || !ReferenceEquals(row.Source, source.Source) ||
                        !SamePose(row.ToSource, source.ToSource) || row.Bone < 0 || row.Bone >= donorPoses.Length ||
                        !donorBones[row.Bone] ||
                        Enumerable.Range(0, result.Count).Any(index =>
                            ReferenceEquals(result[index], donorBones[row.Bone]) &&
                            SamePose(authored[index], donorPoses[row.Bone] * source.ToSource)) ||
                        source.ToTarget[row.Bone] != result.Count || !SamePose(authored[result.Count], donorPoses[row.Bone] * source.ToSource))
                        throw new InvalidOperationException("An appended native bone differs from its actual donor binding.");
                    result.Add(donorBones[row.Bone]);
                }
                var common = 0;
                for (var index = 0; index < donorBones.Length; index++)
                {
                    var mapped = source.ToTarget[index]; if (mapped == -1) continue;
                    if (mapped < 0 || mapped >= result.Count || !donorBones[index] ||
                        !ReferenceEquals(result[mapped], donorBones[index]) ||
                        !SamePose(authored[mapped], donorPoses[index] * source.ToSource))
                        throw new InvalidOperationException("A coupled donor's live bone reference or bind pose changed.");
                    if (mapped < original.Length) common++;
                }
                if (common == 0) throw new InvalidOperationException("A coupled donor has no proven common native frame.");
                return additions.Length == 0 ? null : result.ToArray();

                static bool SamePose(Matrix4x4 a, Matrix4x4 b) => Enumerable.Range(0, 16).All(index =>
                    Finite(a[index]) && Finite(b[index]) && Mathf.Abs(a[index] - b[index]) <= 0.000001f);
            }

            private sealed class NativeBindingRow
            {
                internal int Index;
                internal object Deformer;
                internal UnityEngine.Object Source;
                internal Mesh Original, Replacement;
                internal SurfaceBinding Binding;
            }
            private sealed class NativeBindingRecipe
            {
                internal object Deformer;
                internal UnityEngine.Object Original;
                internal Component[] Children;
                internal UnityEngine.Object[] Sources;
                internal NativeBindingRow[] Changed;
                private string _originalHash;
                private int[] _contexts;
                internal string Key(Func<object, string> fingerprint)
                {
                    _originalHash = fingerprint(Original);
                    _contexts = Children.Select(child => (int)Invoke(Get(child, "Deformer"), "GetDataHash")).ToArray();
                    return _originalHash + ":" + string.Join(":", Sources.Select(fingerprint)) + ":" +
                           string.Join(":", Changed.Select(row => row.Index + ":" + row.Binding.Fingerprint)) +
                           ":context:" + string.Join(":", _contexts);
                }
                internal bool IsCurrent()
                {
                    if (!Original || !ReferenceEquals(Get(Deformer, "MeshData"), Original)) return false;
                    var children = ((IEnumerable)Get(Deformer, "renderDeformerList")).Cast<Component>().ToArray();
                    return children.SequenceEqual(Children) && Changed.All(row => row.Original && row.Replacement &&
                        ReferenceEquals(Get(row.Deformer, "MeshData"), row.Source) && SourceMesh.GetValue(row.Deformer) as Mesh == row.Original);
                }
                internal bool HasSameContext() => _contexts.SequenceEqual(Children.Select(child => (int)Invoke(Get(child, "Deformer"), "GetDataHash")));
                internal UnityEngine.Object[] Create()
                {
                    UnityEngine.Object[] result = null;
                    var values = Create(false, () => true, data => result = data);
                    try
                    {
                        while (values.MoveNext()) throw new InvalidOperationException("A synchronous native binding unexpectedly requested worker preparation.");
                    }
                    finally { (values as IDisposable)?.Dispose(); }
                    return result;
                }
                internal IEnumerator Create(bool asynchronous, Func<bool> current, Action<UnityEngine.Object[]> receive)
                {
                    var created = new List<UnityEngine.Object>();
                    var transferred = false;
                    try
                    {
                        if (!current()) yield break;
                        var virtualData = CreateNativeData(Original);
                        created.Add(virtualData);
                        var oldChildren = (IList)Get(Original, "childDataList");
                        var childData = (IList)Activator.CreateInstance(oldChildren.GetType());
                        foreach (var child in oldChildren) childData.Add(child);
                        Put(virtualData, "childDataList", childData);
                        foreach (var row in Changed)
                        {
                            if (!current()) yield break;
                            var renderData = CreateNativeData(row.Source);
                            created.Add(renderData);
                            RebuildRenderData(renderData, row.Replacement);
                            var prediction = typeof(object).GetMethod("MemberwiseClone", Fields).Invoke(row.Deformer, null);
                            SourceMesh.SetValue(prediction, row.Replacement);
                            Put(prediction, "MeshData", renderData);
                            object rebuilt = null;
                            if (asynchronous)
                                yield return PrepareNativeChild(childData[row.Index], row.Binding, row.Original,
                                    (Vector3)Get(renderData, "baseScale"), (int)Get(virtualData, "vertexCount"), current, value => rebuilt = value);
                            if (!current()) yield break;
                            rebuilt ??= RebuildChildData(childData[row.Index], row.Binding, row.Original,
                                (Vector3)Get(renderData, "baseScale"), (int)Get(virtualData, "vertexCount"));
                            Put(rebuilt, "childDataHash", (int)Invoke(prediction, "GetDataHash"));
                            childData[row.Index] = rebuilt;
                        }
                        Invoke(virtualData, "CreateVerifyData");
                        string originalAfter = null;
                        if (asynchronous) yield return PrepareNativeFingerprint(Original, current, value => originalAfter = value);
                        if (!current()) yield break;
                        originalAfter ??= HashNativeData(Original);
                        if (originalAfter != _originalHash)
                            throw new InvalidOperationException("Derived native binding construction modified its immutable source.");
                        if (asynchronous && !HasSameContext()) yield break;
                        transferred = true;
                        receive(created.ToArray());
                    }
                    finally
                    {
                        if (!transferred) foreach (var data in created) if (data) UnityEngine.Object.Destroy(data);
                    }
                }
            }
            private static NativeBindingRecipe DescribeNativeBinding(object deformer, Component[] children,
                IReadOnlyDictionary<SkinnedMeshRenderer, (SkinMeshSource Source, Mesh Mesh)> changes,
                IReadOnlyDictionary<SkinnedMeshRenderer, SurfaceBinding> bindings)
            {
                var rows = new List<NativeBindingRow>();
                for (var index = 0; index < children.Length; index++)
                {
                    var component = children[index];
                    var child = Get(component, "Deformer");
                    var renderer = ResolveNativeRenderer(component, child) as SkinnedMeshRenderer;
                    if (!renderer || !changes.TryGetValue(renderer, out var change) || !bindings.TryGetValue(renderer, out var binding)) continue;
                    rows.Add(new NativeBindingRow { Index = index, Deformer = child, Source = Get(child, "MeshData") as UnityEngine.Object,
                        Original = change.Source.Original, Replacement = change.Mesh, Binding = binding });
                }
                return rows.Count == 0 ? null : new NativeBindingRecipe { Deformer = deformer,
                    Original = Get(deformer, "MeshData") as UnityEngine.Object, Children = children,
                    Sources = children.Select(child => Get(Get(child, "Deformer"), "MeshData") as UnityEngine.Object).ToArray(), Changed = rows.ToArray() };
            }
            internal static IEnumerator PrepareNativeBindings(IEnumerable<GameObject> roots,
                IReadOnlyDictionary<SkinnedMeshRenderer, (SkinMeshSource Source, Mesh Mesh)> changes,
                IReadOnlyDictionary<SkinnedMeshRenderer, SurfaceBinding> bindings,
                Func<string, bool> retainExisting, Action<string, UnityEngine.Object[]> retainPrepared,
                Func<UnityEngine.Object, IEnumerator> prepareFingerprint,
                Func<UnityEngine.Object, Func<string>, string> sourceFingerprint, Func<bool> current)
            {
                if (VirtualType == null || changes.Count == 0) yield break;
                foreach (var pair in bindings)
                {
                    if (!changes.TryGetValue(pair.Key, out var change) || pair.Value == null ||
                        pair.Value.Vertices.Length != change.Mesh.vertexCount || pair.Value.SourceTangents.Length != change.Source.Original.vertexCount)
                        throw new InvalidOperationException("An authored source attachment does not match its render source.");
                    ValidateBinding(change.Source.Original, change.Mesh, pair.Value, change.Source.ClothOwner, null);
                }
                string Fingerprint(object source) => source is UnityEngine.Object native
                    ? sourceFingerprint(native, () => HashNativeData(source)) : HashNativeData(source);
                foreach (var component in roots.Where(root => root).SelectMany(root => root.GetComponentsInChildren(VirtualType, true)).Cast<Component>().Distinct())
                {
                    if (!current()) yield break;
                    var deformer = Optional(component, "Deformer");
                    // A removed part can remain in the hierarchy until native Destroy
                    // finishes this frame. Only fully resolved current recipes qualify.
                    if (deformer == null || Optional(deformer, "renderDeformerList") is not IEnumerable values) continue;
                    var children = values.Cast<Component>().ToArray();
                    if (children.Any(child => !child || Optional(child, "Deformer") == null)) continue;
                    var recipe = DescribeNativeBinding(deformer, children, changes, bindings);
                    if (recipe == null) continue;
                    bool RecipeCurrent() => current() && recipe.IsCurrent();
                    if (!RecipeCurrent()) continue;
                    foreach (var source in new[] { recipe.Original }.Concat(recipe.Sources).Where(source => source).Distinct())
                    {
                        if (!RecipeCurrent()) break;
                        yield return prepareFingerprint(source);
                    }
                    if (!RecipeCurrent()) continue;
                    var key = recipe.Key(Fingerprint);
                    if (retainExisting(key))
                    {
                        var originalChildren = (IList)Get(recipe.Original, "childDataList");
                        foreach (var row in recipe.Changed)
                        {
                            if (!RecipeCurrent()) break;
                            if (row.Binding.SurfacePlan != null || !row.Binding.Vertices.Any(vertex =>
                                    !vertex.Native && vertex.Sources == null && vertex.SourceUv.HasValue)) continue;
                            yield return PrepareNativeChild(originalChildren[row.Index], row.Binding, row.Original,
                                (Vector3)Get(row.Source, "baseScale"), (int)Get(recipe.Original, "vertexCount"), RecipeCurrent,
                                _ => { }, true);
                        }
                        continue;
                    }
                    yield return recipe.Create(true, RecipeCurrent, data => retainPrepared(key, data));
                }
            }

            private HashSet<int> NativeSkinBoundaryVertices(SkinMeshSource source, SurfaceBinding binding)
            {
                var requested = new HashSet<int>(binding.BoundaryOverrides?.Keys ?? Enumerable.Empty<int>());
                if (binding.AuthoredBase)
                    for (var index = 0; index < source.Original.vertexCount; index++)
                        if (!binding.Vertices[index].Native) requested.Add(index);
                if (requested.Count == 0) return null;
                var result = new HashSet<int>(requested);
                var linked = _owners.Where(owner => owner.Order == 1 && owner.Children.Contains(source.ClothOwner)).ToArray();
                if (linked.Length == 0) return [];
                foreach (var owner in linked)
                {
                    var cloths = _owners.Where(cloth => cloth.Order == 0 && cloth.Parent == owner.Component).ToArray();
                    if (cloths.Length == 0) return [];
                    var registrations = new List<IEnumerable<int>>();
                    foreach (var cloth in cloths)
                    {
                        if (cloth.ClothData == null || Get(cloth.ClothData, "useVertexList") is not IEnumerable vertices)
                            throw new InvalidOperationException("A boundary cloth owner has no immutable vertex registration data.");
                        var indices = vertices.Cast<int>().ToArray();
                        registrations.Add(indices);
                        _boundaryClothVertices[cloth] = indices;
                    }
                    var data = Get(owner.Deformer, "MeshData");
                    var children = (IList)Get(data, "childDataList");
                    var child = children[Array.IndexOf(owner.Children, source.ClothOwner)];
                    result.IntersectWith(NativeSkinOnlyBoundaryVertices(binding, child, (int)Get(data, "vertexCount"), registrations, requested));
                    _boundaryVirtualOwners.Add(owner.Component);
                    _boundaryRenderOwners.Add(source.ClothOwner);
                }
                return result;
            }

            private static HashSet<int> NativeSkinOnlyBoundaryVertices(SurfaceBinding binding, object child,
                int virtualCount, IEnumerable<IEnumerable<int>> registrations, IEnumerable<int> requestedVertices = null)
            {
                if (child == null || registrations == null)
                    throw new InvalidOperationException("A boundary cloth binding has unresolved native consumers.");
                var used = new HashSet<int>(); var consumers = 0;
                foreach (var indices in registrations)
                {
                    if (indices == null) throw new InvalidOperationException("A boundary cloth consumer has unknown vertex registrations.");
                    consumers++;
                    foreach (var index in indices)
                    {
                        if (index < 0 || index >= virtualCount)
                            throw new InvalidOperationException("A boundary cloth consumer registers an invalid virtual vertex.");
                        used.Add(index);
                    }
                }
                if (consumers == 0) throw new InvalidOperationException("A boundary cloth binding has no resolved native consumer.");
                var info = (uint[])Get(child, "vertexInfoList");
                var weights = (Array)Get(child, "vertexWeightList");
                var result = new HashSet<int>();
                foreach (var index in requestedVertices ?? binding.BoundaryOverrides.Keys)
                {
                    var anchor = binding.Vertices[index].Anchor;
                    if (anchor < 0 || anchor >= info.Length)
                        throw new InvalidOperationException("A boundary cloth vertex has no native child correspondence.");
                    var packed = info[anchor]; var count = (int)(packed >> 28); var start = (int)(packed & 0x0fffffff);
                    if (count == 0 || start > weights.Length - count)
                        throw new InvalidOperationException("A boundary cloth vertex has an invalid virtual parent span.");
                    var nativeOnly = true;
                    for (var offset = 0; offset < count; offset++)
                    {
                        var parent = (int)Get(weights.GetValue(start + offset), "parentIndex");
                        if (parent < 0 || parent >= virtualCount)
                            throw new InvalidOperationException("A boundary cloth vertex references an invalid virtual parent.");
                        if (used.Contains(parent)) nativeOnly = false;
                    }
                    if (nativeOnly) result.Add(index);
                }
                return result;
            }

            private static void ValidateBinding(Mesh original, Mesh authored, SurfaceBinding binding, bool nativeCloth,
                ISet<int> nativeSkinOnlyVertices = null)
            {
                var multiSource = binding.Vertices.Any(vertex => vertex.Sources != null);
                if (multiSource && nativeCloth)
                    throw new InvalidOperationException("Multi-source surface attachments require a noncloth source owner.");
                if (multiSource) ValidateSurfaceSources(original, binding.BoundarySource ? binding.BoundarySource : authored, binding);
                if (binding.AuthoredBase) ValidateAuthoredBase(original, binding);
                if (binding.BoundaryOverrides != null) ValidateBoundary(original, authored, binding);
                var sourceCounts = original.GetBonesPerVertex(); var sourceWeights = original.GetAllBoneWeights();
                var targetCounts = authored.GetBonesPerVertex(); var targetWeights = authored.GetAllBoneWeights();
                var boneCount = binding.PaletteAdditions is { Length: > 0 } ? authored.bindposes.Length : original.bindposes.Length;
                if (sourceCounts.Length != original.vertexCount || targetCounts.Length != binding.Vertices.Length)
                    throw new InvalidOperationException("An authored source skin has incomplete per-vertex influence counts.");
                var sourceStarts = new int[sourceCounts.Length];
                for (var index = 1; index < sourceStarts.Length; index++) sourceStarts[index] = sourceStarts[index - 1] + sourceCounts[index - 1];
                var encodingSource = binding.SkinEncodingSource;
                var prepared = multiSource ? encodingSource.PreparedSkin : null;
                if (prepared != null)
                {
                    if (prepared.BoneCount != boneCount || !prepared.MatchesContributions(binding.Vertices) ||
                        prepared.Counts.Length != sourceCounts.Length || prepared.Weights.Length != sourceWeights.Length) prepared = null;
                    else
                    {
                        for (var index = 0; index < sourceCounts.Length; index++) if (prepared.Counts[index] != sourceCounts[index]) { prepared = null; break; }
                        if (prepared != null)
                            for (var index = 0; index < sourceWeights.Length; index++) if (!prepared.Weights[index].Equals(sourceWeights[index])) { prepared = null; break; }
                    }
                }
                var preparedNow = false;
                if (multiSource && prepared == null)
                {
                    prepared = PrepareSurfaceSkinEncoding(original, authored, binding, sourceCounts.ToArray(), sourceWeights.ToArray(), boneCount);
                    preparedNow = true;
                }
                var targetOffset = 0;
                for (var index = 0; index < binding.Vertices.Length; index++)
                {
                    var anchor = binding.Vertices[index].Anchor;
                    if (anchor < 0 || anchor >= sourceCounts.Length)
                        throw new InvalidOperationException("An authored source attachment has no native skin anchor.");
                    if (sourceStarts[anchor] > sourceWeights.Length - sourceCounts[anchor] ||
                        targetOffset > targetWeights.Length - targetCounts[index])
                        throw new InvalidOperationException("An authored source attachment changed native skin binding correspondence.");
                    if (binding.BoundaryOverrides != null && binding.BoundaryOverrides.TryGetValue(index, out var boundary))
                    {
                        var weight = boundary.Weights;
                        var indices = new[] { weight.boneIndex0, weight.boneIndex1, weight.boneIndex2, weight.boneIndex3 };
                        var values = new[] { weight.weight0, weight.weight1, weight.weight2, weight.weight3 };
                        var influence = 0; var total = 0f;
                        for (var slot = 0; slot < values.Length; slot++)
                        {
                            var value = values[slot];
                            if (!Finite(value) || value < 0f || value > 1f ||
                                value > 0f && (indices[slot] < 0 || indices[slot] >= boneCount))
                                throw new InvalidOperationException("A boundary skin declaration has an invalid bone or weight.");
                            if (value == 0f) continue;
                            for (var previous = 0; previous < slot; previous++)
                                if (values[previous] > 0f && indices[previous] == indices[slot])
                                    throw new InvalidOperationException("A boundary skin declaration repeats a bone influence.");
                            if (influence >= targetCounts[index] ||
                                targetWeights[targetOffset + influence].boneIndex != indices[slot] ||
                                targetWeights[targetOffset + influence].weight != value)
                                throw new InvalidOperationException("A boundary skin differs from its declared influence order or weight.");
                            total += value; influence++;
                        }
                        if (influence != targetCounts[index] || Mathf.Abs(total - 1f) > 0.00001f)
                            throw new InvalidOperationException("A boundary skin declaration has incomplete or unnormalized influences.");
                        if (nativeCloth && (nativeSkinOnlyVertices == null || !nativeSkinOnlyVertices.Contains(index)) &&
                            (sourceCounts[anchor] != targetCounts[index] ||
                            Enumerable.Range(0, sourceCounts[anchor]).Any(offset =>
                                !sourceWeights[sourceStarts[anchor] + offset].Equals(targetWeights[targetOffset + offset]))))
                            throw new InvalidOperationException("A boundary cloth skin change requires matching virtual skin binding data.");
                        if (nativeCloth && binding.BoundaryPositions != null &&
                            binding.BoundaryPositions.TryGetValue(index, out var position) && !position.Original.Equals(position.Position) &&
                            (nativeSkinOnlyVertices == null || !nativeSkinOnlyVertices.Contains(index)))
                            throw new InvalidOperationException("A boundary cloth position change requires native-only skin binding data.");
                        if (nativeCloth && binding.BoundaryBlendShapePositions != null &&
                            binding.BoundaryBlendShapePositions.TryGetValue(index, out var framePosition) &&
                            !framePosition.Original.Equals(framePosition.Position) &&
                            (nativeSkinOnlyVertices == null || !nativeSkinOnlyVertices.Contains(index)))
                            throw new InvalidOperationException("A boundary cloth frame position change requires native-only skin binding data.");
                    }
                    else if (multiSource && !binding.Vertices[index].Native)
                    {
                        var expected = prepared.Expected[index];
                        var count = (expected.weight0 > 0f ? 1 : 0) + (expected.weight1 > 0f ? 1 : 0) +
                            (expected.weight2 > 0f ? 1 : 0) + (expected.weight3 > 0f ? 1 : 0);
                        if (targetCounts[index] != count) throw new InvalidOperationException("A multi-source surface changed canonical positive support.");
                        for (var slot = 0; slot < count; slot++)
                        {
                            var bone = slot switch { 0 => expected.boneIndex0, 1 => expected.boneIndex1, 2 => expected.boneIndex2, _ => expected.boneIndex3 };
                            var value = slot switch { 0 => expected.weight0, 1 => expected.weight1, 2 => expected.weight2, _ => expected.weight3 };
                            if (targetWeights[targetOffset + slot].boneIndex != bone || targetWeights[targetOffset + slot].weight != value)
                                throw new InvalidOperationException("A multi-source surface changed its canonical native skin weight or order.");
                        }
                    }
                    else
                    {
                        if (sourceCounts[anchor] != targetCounts[index])
                            throw new InvalidOperationException("An authored source attachment changed native skin binding correspondence.");
                        for (var influence = 0; influence < sourceCounts[anchor]; influence++)
                            if (!sourceWeights[sourceStarts[anchor] + influence].Equals(targetWeights[targetOffset + influence]))
                                throw new InvalidOperationException("An authored source attachment changed a native skin weight or its order.");
                    }
                    targetOffset += targetCounts[index];
                }
                if (targetOffset != targetWeights.Length) throw new InvalidOperationException("An authored source skin binding has unused influences.");
                if (nativeCloth)
                {
                    // New surfaces use the native worker basis; unchanged native vertices keep their original frame.
                    var tangents = authored.tangents;
                    var frameHand = Mathf.Sign(binding.RendererToPrefab.determinant);
                    if (tangents.Length != authored.vertexCount)
                        throw new InvalidOperationException("An authored cloth attachment has incomplete tangent frames.");
                    for (var index = 0; index < tangents.Length; index++)
                        if (binding.AuthoredBase && index < original.vertexCount && !binding.Vertices[index].Native &&
                            (nativeSkinOnlyVertices == null || !nativeSkinOnlyVertices.Contains(index)))
                            throw new InvalidOperationException("An authored native base changed a surface consumed by cloth simulation.");
                        else if (binding.BoundaryOverrides != null && binding.BoundaryOverrides.TryGetValue(index, out var boundary)
                                ? !tangents[index].Equals(boundary.Tangent)
                                : binding.Vertices[index].Native
                                ? !tangents[index].Equals(binding.SourceTangents[binding.Vertices[index].Anchor])
                                : binding.AuthoredBase && index < original.vertexCount
                                ? tangents[index].w != binding.SourceTangents[index].w
                                : tangents[index].w != OwnedTangentHand(frameHand, binding.Vertices[index]))
                            throw new InvalidOperationException("An authored cloth attachment changed its owned or native tangent frame.");
                }
                if (preparedNow && ReferenceEquals(encodingSource.Vertices, binding.Vertices))
                    encodingSource.PreparedSkin = prepared;
            }

            private static float OwnedTangentHand(float frameHand, SurfaceVertex vertex)
            {
                if (!vertex.SourceUv.HasValue) return -1f;
                if (vertex.TangentW is not (-1f or 1f))
                    throw new InvalidOperationException("An owned source surface has invalid tangent handedness.");
                return vertex.TangentW * frameHand;
            }

            private static Vector3 GeometryUnit(Vector3 value)
            {
                // Cross products carry square metres. Unity's vector epsilon
                // would reject valid small hair triangles based on model size.
                var scale = Mathf.Max(Mathf.Abs(value.x), Mathf.Max(Mathf.Abs(value.y), Mathf.Abs(value.z)));
                if (!Finite(scale) || scale <= 0f) return Vector3.zero;
                value /= scale;
                return value / (float)Math.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z);
            }

            private static void ValidateAuthoredBase(Mesh original, SurfaceBinding binding)
            {
                var authored = binding.AuthoredBase; var count = original.vertexCount;
                if (!authored || !authored.isReadable || authored.vertexCount != count || binding.Vertices.Length < count ||
                    original.subMeshCount != authored.subMeshCount || !original.bindposes.SequenceEqual(authored.bindposes) ||
                    !original.boneWeights.SequenceEqual(authored.boneWeights) || original.blendShapeCount != authored.blendShapeCount)
                    throw new InvalidOperationException("An authored base has no exact native topology, skin and physical source layout.");
                var proofOwner = binding.SkinEncodingSource;
                var cache = proofOwner.CacheAuthoredBaseValidation && ReferenceEquals(proofOwner.AuthoredBase, authored);
                if (cache && proofOwner.BaseProof?.Matches(original, authored, binding) == true) return;
                RacesContext.ReadNativeRestMesh(original, source =>
                {
                    var positions = source.vertices; var normals = source.normals; var tangents = binding.SourceTangents;
                    var targetPositions = authored.vertices; var targetNormals = authored.normals; var targetTangents = authored.tangents;
                    if (positions.Length != count || normals.Length != count || tangents.Length != count ||
                        targetPositions.Length != count || targetNormals.Length != count || targetTangents.Length != count)
                        throw new InvalidOperationException("An authored base has incomplete geometric frames.");
                    for (var slot = 0; slot < source.subMeshCount; slot++)
                        if (source.GetTopology(slot) != authored.GetTopology(slot) || !source.GetIndices(slot).SequenceEqual(authored.GetIndices(slot)))
                            throw new InvalidOperationException("An authored base changed native submesh topology.");
                    for (var index = 0; index < count; index++)
                    {
                        var vertex = binding.Vertices[index];
                        if (vertex.Anchor != index || vertex.Sources != null || !Finite(vertex.Offset) || !Finite(vertex.Normal) || !Finite(vertex.Tangent) ||
                            !Finite(positions[index]) || !Finite(normals[index]) || !Finite((Vector3)tangents[index]) ||
                            !Finite(targetPositions[index]) || !Finite(targetNormals[index]) || !Finite((Vector3)targetTangents[index]) ||
                            targetTangents[index].w != tangents[index].w)
                            throw new InvalidOperationException("An authored base has no finite identity native anchor and handedness.");
                        if (vertex.Native)
                        {
                            if (!vertex.Offset.Equals(Vector3.zero) || !vertex.Normal.Equals(Vector3.forward) || !vertex.Tangent.Equals(Vector3.right) ||
                                !positions[index].Equals(targetPositions[index]) || !normals[index].Equals(targetNormals[index]) || !tangents[index].Equals(targetTangents[index]))
                                throw new InvalidOperationException("An authored base marked a changed native sample as unchanged.");
                            continue;
                        }
                        var normal = normals[index].normalized; var tangent = (Vector3)tangents[index];
                        tangent = (tangent - normal * Vector3.Dot(normal, tangent)).normalized;
                        var bitangent = Vector3.Cross(normal, tangent) * tangents[index].w;
                        if (normal.sqrMagnitude < 0.99f || tangent.sqrMagnitude < 0.99f || bitangent.sqrMagnitude < 0.99f || tangents[index].w is not (-1f or 1f))
                            throw new InvalidOperationException("An authored base has a singular native frame.");
                        Vector3 Transfer(Vector3 value) => tangent * value.x + bitangent * value.y + normal * value.z;
                        if (!SameSurface(positions[index] + Transfer(vertex.Offset), targetPositions[index]) ||
                            !SameSurface(Transfer(vertex.Normal), targetNormals[index]) || !SameSurface(Transfer(vertex.Tangent), (Vector3)targetTangents[index]))
                            throw new InvalidOperationException("An authored base differs from its declared native frame reconstruction.");
                    }
                    var a = new List<Vector4>(); var b = new List<Vector4>();
                    for (var channel = 0; channel < 8; channel++)
                    {
                        source.GetUVs(channel, a); authored.GetUVs(channel, b);
                        if (a.Count != b.Count || !a.SequenceEqual(b) || a.Count != 0 &&
                            source.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel)) !=
                            authored.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel)))
                            throw new InvalidOperationException("An authored base changed a native UV channel.");
                    }
                    if (!source.colors.SequenceEqual(authored.colors)) throw new InvalidOperationException("An authored base changed native colours.");
                    return true;
                });
                var sp = new Vector3[count]; var sn = new Vector3[count]; var st = new Vector3[count];
                var tp = new Vector3[count]; var tn = new Vector3[count]; var tt = new Vector3[count];
                for (var channel = 0; channel < original.blendShapeCount; channel++)
                {
                    var frames = original.GetBlendShapeFrameCount(channel);
                    if (original.GetBlendShapeName(channel) != authored.GetBlendShapeName(channel) || frames != authored.GetBlendShapeFrameCount(channel))
                        throw new InvalidOperationException("An authored base changed its native physical layout.");
                    for (var frame = 0; frame < frames; frame++)
                    {
                        var weight = original.GetBlendShapeFrameWeight(channel, frame);
                        if (!Finite(weight) || weight != authored.GetBlendShapeFrameWeight(channel, frame)) throw new InvalidOperationException("An authored base changed a native physical frame range.");
                        original.GetBlendShapeFrameVertices(channel, frame, sp, sn, st); authored.GetBlendShapeFrameVertices(channel, frame, tp, tn, tt);
                        for (var index = 0; index < count; index++)
                            if (!Finite(tp[index]) || !Finite(tn[index]) || !Finite(tt[index]) || binding.Vertices[index].Native &&
                                (!sp[index].Equals(tp[index]) || !sn[index].Equals(tn[index]) || !st[index].Equals(tt[index])))
                                throw new InvalidOperationException("An authored base changed an undeclared native physical sample.");
                    }
                }
                if (cache) proofOwner.BaseProof = new SurfaceBinding.AuthoredBaseProof(original, authored, binding);
                return;
                // Same existing binary32 surface-wire round-trip bound as ValidateSurfaceSources.
                static bool SameSurface(Vector3 a, Vector3 b) => Same(a.x, b.x) && Same(a.y, b.y) && Same(a.z, b.z);
                static bool Same(float a, float b) => Finite(a) && Finite(b) && Mathf.Abs(a - b) <= 0.000002f * Mathf.Max(1f, Mathf.Abs(a));
            }

            private static void ValidateSurfaceSources(Mesh original, Mesh authored, SurfaceBinding binding)
            {
                var nativeCount = original.vertexCount;
                if (nativeCount <= 0 || binding.Vertices.Length < nativeCount || authored.vertexCount != binding.Vertices.Length ||
                    !(binding.PaletteAdditions is { Length: > 0 }
                        ? authored.bindposes.Length == original.bindposes.Length + binding.PaletteAdditions.Length &&
                          original.bindposes.SequenceEqual(authored.bindposes.Take(original.bindposes.Length))
                        : original.bindposes.SequenceEqual(authored.bindposes)) || binding.SourceTangents.Length != nativeCount)
                    throw new InvalidOperationException("A multi-source surface changed its native prefix or bind poses.");
                for (var index = 0; index < binding.Vertices.Length; index++)
                {
                    var vertex = binding.Vertices[index]; var sources = vertex.Sources;
                    if (binding.IsForeignBoundaryVertex(index, nativeCount)) continue;
                    if (sources == null || sources.Length is < 1 or > 4 || vertex.Native != (index < nativeCount) ||
                        vertex.Anchor != sources[0].Anchor || vertex.TangentW is not (-1f or 1f) ||
                        vertex.Native && (vertex.Anchor != index || sources.Length != 1 || sources[0].Weight != 1f ||
                            !vertex.Offset.Equals(Vector3.zero) || !vertex.Normal.Equals(Vector3.forward) || !vertex.Tangent.Equals(Vector3.right)))
                        throw new InvalidOperationException("A multi-source surface has no exact native prefix or primary frame.");
                    var total = 0d;
                    for (var source = 0; source < sources.Length; source++)
                    {
                        var contribution = sources[source];
                        if (contribution.Anchor < 0 || contribution.Anchor >= nativeCount ||
                            !Finite(contribution.Weight) || contribution.Weight <= 0f || contribution.Weight > 1f)
                            throw new InvalidOperationException("A multi-source surface has an invalid native contribution.");
                        for (var previous = 0; previous < source; previous++)
                            if (sources[previous].Anchor == contribution.Anchor)
                                throw new InvalidOperationException("A multi-source surface repeats a native contribution.");
                        total += contribution.Weight;
                    }
                    if (Math.Abs(total - 1d) > 0.000001d)
                        throw new InvalidOperationException("A multi-source surface has unnormalized native contributions.");
                }
                RacesContext.ReadNativeRestMesh(original, readable =>
                {
                    var positions = readable.vertices; var normals = readable.normals;
                    var tangents = binding.SourceTangents;
                    var targetPositions = authored.vertices; var targetNormals = authored.normals; var targetTangents = authored.tangents;
                    if (positions.Length != nativeCount || normals.Length != nativeCount || targetPositions.Length != authored.vertexCount ||
                        targetNormals.Length != authored.vertexCount || targetTangents.Length != authored.vertexCount)
                        throw new InvalidOperationException("A multi-source surface has incomplete geometric frames.");
                    for (var index = 0; index < binding.Vertices.Length; index++)
                    {
                        var vertex = binding.Vertices[index]; var anchor = vertex.Anchor;
                        if (binding.IsForeignBoundaryVertex(index, nativeCount))
                        {
                            if (!Finite(targetPositions[index]) || !Finite(targetNormals[index]) ||
                                !Finite((Vector3)targetTangents[index]) || targetTangents[index].w is not (-1f or 1f))
                                throw new InvalidOperationException("A coupled donor boundary has an invalid geometric sample.");
                            continue;
                        }
                        if (!Finite(positions[anchor]) || !Finite(normals[anchor]) || !Finite((Vector3)tangents[anchor]) ||
                            !Finite(vertex.Offset) || !Finite(vertex.Normal) || !Finite(vertex.Tangent) ||
                            !Finite(targetPositions[index]) || !Finite(targetNormals[index]) || !Finite((Vector3)targetTangents[index]) ||
                            targetTangents[index].w != vertex.TangentW)
                            throw new InvalidOperationException("A multi-source surface has a nonfinite or incompatible tangent frame.");
                        if (vertex.Native)
                        {
                            if (!positions[index].Equals(targetPositions[index]) || !normals[index].Equals(targetNormals[index]) ||
                                !tangents[index].Equals(targetTangents[index]))
                                throw new InvalidOperationException("A multi-source surface changed a native geometric sample.");
                            continue;
                        }
                        var normal = normals[anchor].normalized;
                        var tangent = ((Vector3)tangents[anchor]).normalized;
                        var bitangent = Vector3.Cross(normal, tangent) * tangents[anchor].w;
                        if (normal.sqrMagnitude < 0.99f || tangent.sqrMagnitude < 0.99f || bitangent.sqrMagnitude < 0.00000001f ||
                            tangents[anchor].w is not (-1f or 1f))
                            throw new InvalidOperationException("A multi-source surface has a singular primary native frame.");
                        Vector3 Transfer(Vector3 value) => tangent * value.x + bitangent * value.y + normal * value.z;
                        if (!SameSurface(positions[anchor] + Transfer(vertex.Offset), targetPositions[index]) ||
                            !SameSurface(Transfer(vertex.Normal), targetNormals[index]) ||
                            !SameSurface(Transfer(vertex.Tangent), (Vector3)targetTangents[index]))
                            throw new InvalidOperationException("A multi-source surface differs from its declared primary native frame.");
                    }
                    // V2 appends samples; every original UV/color sample remains owned
                    // by the immutable native prefix, independent of the new topology.
                    var sourceUv = new List<Vector4>(); var targetUv = new List<Vector4>();
                    for (var channel = 0; channel < 8; channel++)
                    {
                        readable.GetUVs(channel, sourceUv); authored.GetUVs(channel, targetUv);
                        if (sourceUv.Count == 0 && targetUv.Count == 0) continue;
                        if (sourceUv.Count != nativeCount || targetUv.Count != authored.vertexCount ||
                            readable.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel)) !=
                            authored.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel)) ||
                            sourceUv.Where((value, index) => !value.Equals(targetUv[index])).Any())
                            throw new InvalidOperationException("A multi-source surface changed a native UV sample or channel.");
                    }
                    var colors = readable.colors; var targetColors = authored.colors;
                    if (colors.Length != 0 || targetColors.Length != 0)
                        if (colors.Length != nativeCount || targetColors.Length != authored.vertexCount ||
                            colors.Where((value, index) => !value.Equals(targetColors[index])).Any())
                            throw new InvalidOperationException("A multi-source surface changed a native color sample.");
                    return true;
                });
                if (original.blendShapeCount != authored.blendShapeCount)
                    throw new InvalidOperationException("A multi-source surface changed its native physical channels.");
                var sourcePosition = new Vector3[nativeCount]; var sourceNormal = new Vector3[nativeCount]; var sourceTangent = new Vector3[nativeCount];
                var targetPosition = new Vector3[authored.vertexCount]; var targetNormal = new Vector3[authored.vertexCount]; var targetTangent = new Vector3[authored.vertexCount];
                for (var shape = 0; shape < original.blendShapeCount; shape++)
                {
                    var frames = original.GetBlendShapeFrameCount(shape);
                    if (original.GetBlendShapeName(shape) != authored.GetBlendShapeName(shape) || frames != authored.GetBlendShapeFrameCount(shape))
                        throw new InvalidOperationException("A multi-source surface changed its physical frame layout.");
                    for (var frame = 0; frame < frames; frame++)
                    {
                        var weight = original.GetBlendShapeFrameWeight(shape, frame);
                        if (!Finite(weight) || weight != authored.GetBlendShapeFrameWeight(shape, frame))
                            throw new InvalidOperationException("A multi-source surface changed its physical frame weight.");
                        original.GetBlendShapeFrameVertices(shape, frame, sourcePosition, sourceNormal, sourceTangent);
                        authored.GetBlendShapeFrameVertices(shape, frame, targetPosition, targetNormal, targetTangent);
                        for (var index = 0; index < authored.vertexCount; index++)
                        {
                            if (!Finite(targetPosition[index]) || !Finite(targetNormal[index]) || !Finite(targetTangent[index]) ||
                                index < nativeCount && (!sourcePosition[index].Equals(targetPosition[index]) ||
                                    !sourceNormal[index].Equals(targetNormal[index]) || !sourceTangent[index].Equals(targetTangent[index])))
                                throw new InvalidOperationException("A multi-source surface changed or invalidated a native physical sample.");
                        }
                    }
                }
                // The wire uses binary32 coordinates computed from a double-precision
                // native frame. Allow only its bounded float arithmetic round trip.
                static bool SameSurface(Vector3 expected, Vector3 actual) =>
                    Finite(expected) && Finite(actual) && Same(expected.x, actual.x) && Same(expected.y, actual.y) && Same(expected.z, actual.z);
                static bool Same(float expected, float actual) => Mathf.Abs(expected - actual) <=
                    0.000002f * Mathf.Max(1f, Mathf.Abs(expected));
            }

            private static void ValidateBoundary(Mesh original, Mesh authored, SurfaceBinding binding)
            {
                var source = binding.BoundarySource;
                if (!source || source.vertexCount != authored.vertexCount || source.subMeshCount != authored.subMeshCount ||
                    !source.bindposes.SequenceEqual(authored.bindposes) ||
                    !(binding.PaletteAdditions is { Length: > 0 }
                        ? source.bindposes.Take(original.bindposes.Length).SequenceEqual(original.bindposes)
                        : source.bindposes.SequenceEqual(original.bindposes)))
                    throw new InvalidOperationException("A boundary attachment changed its positions, topology, or bind poses.");
                var sourcePositions = source.vertices; var positions = authored.vertices;
                if (sourcePositions.Length != source.vertexCount || positions.Length != source.vertexCount)
                    throw new InvalidOperationException("A boundary attachment has incomplete positions.");
                if (binding.BoundaryPositions != null)
                {
                    var frame = binding.BoundaryPositionFrame;
                    for (var component = 0; component < 16; component++)
                        if (!Finite(frame[component])) throw new InvalidOperationException("A boundary position frame is nonfinite.");
                    if (frame[3] != 0f || frame[7] != 0f || frame[11] != 0f || frame[15] != 1f ||
                        !Finite(frame.determinant) || frame.determinant == 0f ||
                        binding.BoundaryPositions.Keys.Any(index => index < 0 || index >= source.vertexCount))
                        throw new InvalidOperationException("A boundary position frame or vertex is invalid.");
                }
                for (var index = 0; index < source.vertexCount; index++)
                {
                    if (binding.BoundaryPositions != null && binding.BoundaryPositions.TryGetValue(index, out var position))
                    {
                        var distance = binding.BoundaryPositionFrame.MultiplyVector(position.Position - position.Original).magnitude;
                        if (!Finite(position.Original) || !Finite(position.Position) || !Finite(position.MaximumDistance) ||
                            position.MaximumDistance < 0f || !Finite(distance) || distance > position.MaximumDistance ||
                            !sourcePositions[index].Equals(position.Original) || !positions[index].Equals(position.Position))
                            throw new InvalidOperationException("A boundary position differs from its declared source, target, or distance bound.");
                    }
                    else if (!sourcePositions[index].Equals(positions[index]))
                        throw new InvalidOperationException("A boundary attachment changed an undeclared position.");
                }
                for (var index = 0; index < source.subMeshCount; index++)
                    if (source.GetTopology(index) != authored.GetTopology(index) ||
                        !source.GetIndices(index).SequenceEqual(authored.GetIndices(index)))
                        throw new InvalidOperationException("A boundary attachment changed its source topology.");
                var sourceUv = new List<Vector4>(); var targetUv = new List<Vector4>();
                for (var channel = 0; channel < 8; channel++)
                {
                    source.GetUVs(channel, sourceUv); authored.GetUVs(channel, targetUv);
                    if (!sourceUv.SequenceEqual(targetUv))
                        throw new InvalidOperationException("A boundary attachment changed its source UVs.");
                }
                var normals = authored.normals; var tangents = authored.tangents;
                var sourceNormals = source.normals; var sourceTangents = source.tangents;
                var sourceWeights = source.boneWeights; var weights = authored.boneWeights;
                if (normals.Length != source.vertexCount || tangents.Length != source.vertexCount ||
                    sourceNormals.Length != source.vertexCount || sourceTangents.Length != source.vertexCount ||
                    weights.Length != source.vertexCount || sourceWeights.Length != source.vertexCount ||
                    binding.BoundaryOverrides.Keys.Any(index => index < 0 || index >= source.vertexCount))
                    throw new InvalidOperationException("A boundary attachment has incomplete or invalid vertex channels.");
                for (var index = 0; index < source.vertexCount; index++)
                {
                    if (binding.BoundaryOverrides.TryGetValue(index, out var boundary))
                    {
                        if (!Finite(boundary.Normal) || !Finite((Vector3)boundary.Tangent) ||
                            boundary.Normal.sqrMagnitude < 0.99f || ((Vector3)boundary.Tangent).sqrMagnitude < 0.99f ||
                            boundary.Tangent.w is not (-1f or 1f) ||
                            !normals[index].Equals(boundary.Normal) || !tangents[index].Equals(boundary.Tangent))
                            throw new InvalidOperationException("A boundary surface differs from its declared normal or tangent.");
                    }
                    else if (!normals[index].Equals(sourceNormals[index]) || !tangents[index].Equals(sourceTangents[index]) ||
                             !weights[index].Equals(sourceWeights[index]))
                        throw new InvalidOperationException("A boundary attachment changed an undeclared vertex channel.");
                }
                if (source.blendShapeCount != authored.blendShapeCount)
                    throw new InvalidOperationException("A boundary attachment changed its expression channels.");
                if (binding.BoundaryBlendShapePositions != null &&
                    source.GetBlendShapeIndex(binding.BoundaryBlendShapeName) < 0)
                    throw new InvalidOperationException("A boundary frame position has no matching source expression.");
                var sourcePosition = new Vector3[source.vertexCount]; var sourceNormal = new Vector3[source.vertexCount];
                var sourceTangent = new Vector3[source.vertexCount]; var targetPosition = new Vector3[source.vertexCount];
                var targetNormal = new Vector3[source.vertexCount]; var targetTangent = new Vector3[source.vertexCount];
                for (var shape = 0; shape < source.blendShapeCount; shape++)
                {
                    var name = source.GetBlendShapeName(shape); var count = source.GetBlendShapeFrameCount(shape);
                    if (name != authored.GetBlendShapeName(shape) || count != authored.GetBlendShapeFrameCount(shape))
                        throw new InvalidOperationException("A boundary attachment changed its expression layout.");
                    for (var frame = 0; frame < count; frame++)
                    {
                        var frameWeight = source.GetBlendShapeFrameWeight(shape, frame);
                        if (frameWeight != authored.GetBlendShapeFrameWeight(shape, frame))
                            throw new InvalidOperationException("A boundary attachment changed its expression frame weights.");
                        source.GetBlendShapeFrameVertices(shape, frame, sourcePosition, sourceNormal, sourceTangent);
                        authored.GetBlendShapeFrameVertices(shape, frame, targetPosition, targetNormal, targetTangent);
                        for (var index = 0; index < source.vertexCount; index++)
                        {
                            if (name == binding.BoundaryBlendShapeName && binding.BoundaryBlendShapePositions != null &&
                                binding.BoundaryBlendShapePositions.TryGetValue(index, out var position))
                            {
                                if (!Finite(frameWeight) || frameWeight == 0f || !Finite(position.Original) || !Finite(position.Position) ||
                                    !(sourcePosition[index] * (100f / frameWeight)).Equals(position.Original) ||
                                    !SameFrameValue(position.Position * (frameWeight / 100f), targetPosition[index], frameWeight))
                                    throw new InvalidOperationException("A boundary frame position differs from its declared source or target.");
                            }
                            else if (!SameFrameValue(sourcePosition[index], targetPosition[index], frameWeight))
                                throw new InvalidOperationException("A boundary attachment changed an undeclared expression position.");
                            if (name == binding.BoundaryBlendShapeName && binding.BoundaryOverrides.ContainsKey(index)
                                    ? !Finite(targetNormal[index]) || !Finite(targetTangent[index])
                                    : !SameFrameValue(sourceNormal[index], targetNormal[index], frameWeight) ||
                                      !SameFrameValue(sourceTangent[index], targetTangent[index], frameWeight))
                                throw new InvalidOperationException("A boundary attachment changed an undeclared expression vertex.");
                        }
                    }
                }
                static bool SameFrameValue(Vector3 source, Vector3 target, float frameWeight) => source.Equals(target) ||
                    target.Equals(Vector3.zero) && Finite(source) && Finite(frameWeight) && frameWeight != 0f &&
                    source * (100f / Mathf.Abs(frameWeight)) == Vector3.zero;
            }

            private static readonly Dictionary<Assembly, Func<Vector3, ulong>> NativeVectorHashes = new();

            private static Func<Vector3, ulong> NativeVectorHash(Assembly assembly)
            {
                lock (NativeVectorHashes)
                {
                    if (NativeVectorHashes.TryGetValue(assembly, out var hash)) return hash;
                    var type = assembly.GetType("MagicaCloth.DataHashExtensions", true);
                    var method = type.GetMethod("GetVectorDataHash", BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(Vector3) }, null) ?? throw new MissingMethodException(type.FullName, "GetVectorDataHash");
                    hash = (Func<Vector3, ulong>)Delegate.CreateDelegate(typeof(Func<Vector3, ulong>), method);
                    NativeVectorHashes.Add(assembly, hash);
                    return hash;
                }
            }

            private static void RebuildRenderData(UnityEngine.Object data, Mesh replacement)
            {
                foreach (var name in new[] { "vertexInfoList", "vertexWeightList", "uvList", "lineList", "triangleList",
                             "vertexToTriangleInfoList", "vertexToTriangleIndexList", "childDataList" })
                    if (Optional(data, name) is IList { Count: > 0 })
                        throw new InvalidOperationException("The native render-data layout has unsupported embedded binding channels.");
                Put(data, "vertexCount", replacement.vertexCount);
                Put(data, "triangleCount", replacement.triangles.Length / 3);
                Put(data, "vertexHashList", replacement.vertices.Select(NativeVectorHash(data.GetType().Assembly)).ToArray());
                Invoke(data, "CreateVerifyData");
            }

            private static readonly Dictionary<Type, Func<NativeChildInput, NativeDataWork, object>> NativeChildDataBuilders = new();
            private static Type _nativeChildType, _nativeWeightType;
            private sealed class NativeChildBinding(SurfaceBinding source, bool copy)
            {
                internal readonly SurfaceVertex[] Vertices = copy ? (SurfaceVertex[])source.Vertices.Clone() : source.Vertices;
                internal readonly Vector4[] SourceTangents = copy ? (Vector4[])source.SourceTangents.Clone() : source.SourceTangents;
                internal readonly Matrix4x4 RendererToPrefab = source.RendererToPrefab;
                internal readonly IReadOnlyDictionary<int, (BoneWeight Weights, Vector3 Normal, Vector4 Tangent)> BoundaryOverrides =
                    copy && source.BoundaryOverrides != null
                        ? source.BoundaryOverrides.ToDictionary(pair => pair.Key, pair => pair.Value) : source.BoundaryOverrides;
            }
            internal readonly struct NativeSurfacePoint(int index, int a, int b, int c, Vector3 bary,
                Vector3 offset, Vector3 normal, Vector3 tangent, float hand, float tangentHand)
            {
                internal readonly int Index = index, A = a, B = b, C = c;
                internal readonly Vector3 Bary = bary, Offset = offset, Normal = normal, Tangent = tangent;
                internal readonly float Hand = hand;
                internal readonly float TangentHand = tangentHand;
            }
            internal sealed class NativeSurfacePlan
            {
                internal readonly NativeSurfacePoint[] Points;
                internal readonly bool[] ReusePreviousFrame;
                internal readonly int NativeCount, TargetCount;
                internal readonly Matrix4x4 SourceFrame;
                internal readonly bool GeometryFrames;
                internal long Bytes => 224L + Points.LongLength * 72L + ReusePreviousFrame.LongLength;

                internal NativeSurfacePlan(NativeSurfacePoint[] points, int nativeCount, int targetCount,
                    Matrix4x4 sourceFrame)
                {
                    var seen = new HashSet<int>(points.Length);
                    foreach (var point in points)
                    {
                        if (point.Index < nativeCount || point.Index >= targetCount || !seen.Add(point.Index) ||
                            point.A < 0 || point.A >= nativeCount || point.B < 0 || point.B >= nativeCount ||
                            point.C < 0 || point.C >= nativeCount || !Finite(point.Bary) ||
                            point.Bary.x < 0 || point.Bary.y < 0 || point.Bary.z < 0 ||
                            Mathf.Abs(point.Bary.x + point.Bary.y + point.Bary.z - 1f) > .000002f ||
                            !Finite(point.Offset) || !Finite(point.Normal) || !Finite(point.Tangent) ||
                            (point.Hand != 1f && point.Hand != -1f && point.Hand != 2f) ||
                            (point.TangentHand != 1f && point.TangentHand != -1f))
                            throw new InvalidOperationException("A final surface plan has an invalid owned point.");
                    }
                    Points = points;
                    GeometryFrames = points.Any(point => point.Hand == 2f);
                    NativeCount = nativeCount;
                    TargetCount = targetCount;
                    SourceFrame = sourceFrame;
                    // Adjacent points share only the exact source interpolant; local
                    // residuals, output order and per-point morphs remain independent.
                    ReusePreviousFrame = new bool[points.Length];
                    for (var index = 1; index < points.Length; index++)
                        ReusePreviousFrame[index] = SameFrame(in points[index - 1], in points[index]);
                }

                private static unsafe int FrameBits(float value) => *(int*)&value;

                private static bool SameFrame(in NativeSurfacePoint first, in NativeSurfacePoint second) =>
                    first.A == second.A && first.B == second.B && first.C == second.C &&
                    FrameBits(first.Bary.x) == FrameBits(second.Bary.x) &&
                    FrameBits(first.Bary.y) == FrameBits(second.Bary.y) &&
                    FrameBits(first.Bary.z) == FrameBits(second.Bary.z) &&
                    FrameBits(first.Hand) == FrameBits(second.Hand);
            }
            private static NativeSurfacePlan PrepareNativeSurfacePlan(NativeChildInput input, NativeDataWork work)
            {
                CheckNativeWork(work);
                var owner = input.SurfaceOwner;
                if (owner?.NativeSurface != null) return owner.NativeSurface;
                if (input.Surface == null) return null;
                PrepareNativeSurface(input, work);
                var nativeCount = input.Binding.SourceTangents.Length;
                // Point capture, uniqueness validation and the adjacent-frame flags coexist until publication.
                var peak = input.Bytes + (long)Math.Max(0, input.Binding.Vertices.Length - nativeCount) * 177L +
                    (long)nativeCount * 48L + 288L;
                if (peak > MaximumNativeWorkBytes) throw new NativeDataPacketLimit();
                var source = input.Surface.Frames; var authored = new NativeSurfaceFrame[nativeCount];
                for (var index = 0; index < nativeCount; index++)
                {
                    if ((index & 255) == 0) CheckNativeWork(work);
                    var vertex = input.Binding.Vertices[index]; var frame = source[vertex.Anchor];
                    if (vertex.Native) { authored[index] = source[index]; continue; }
                    var normal = frame.Transfer(vertex.Normal).normalized; var tangent = frame.Transfer(vertex.Tangent);
                    tangent = (tangent - normal * Vector3.Dot(normal, tangent)).normalized;
                    var hand = Vector3.Dot(Vector3.Cross(frame.Normal, frame.Tangent), frame.Bitangent) < 0f ? -1f : 1f;
                    authored[index] = new NativeSurfaceFrame(frame.Position + frame.Transfer(vertex.Offset), normal,
                        tangent, Vector3.Cross(normal, tangent) * hand);
                }
                var points = new List<NativeSurfacePoint>(Math.Max(0, input.Binding.Vertices.Length - nativeCount));
                var frameHand = Mathf.Sign(input.Binding.RendererToPrefab.determinant);
                var contributions = new NativeSurfaceContribution[3];
                for (var index = nativeCount; index < input.Binding.Vertices.Length; index++)
                {
                    if ((index & 255) == 0) CheckNativeWork(work);
                    var vertex = input.Binding.Vertices[index];
                    if (vertex.Native || vertex.Sources != null || !vertex.SourceUv.HasValue) continue;
                    var count = NativeSurfaceContributions(input, vertex, contributions);
                    var a = contributions[0].Anchor; var b = count > 1 ? contributions[1].Anchor : a;
                    var c = count > 2 ? contributions[2].Anchor : a;
                    var bary = new Vector3(contributions[0].Factor, count > 1 ? contributions[1].Factor : 0f,
                        count > 2 ? contributions[2].Factor : 0f);
                    var position = authored[a].Position * bary.x + authored[b].Position * bary.y + authored[c].Position * bary.z;
                    var normal = (authored[a].Normal * bary.x + authored[b].Normal * bary.y + authored[c].Normal * bary.z).normalized;
                    var tangent = authored[a].Tangent * bary.x + authored[b].Tangent * bary.y + authored[c].Tangent * bary.z;
                    tangent = (tangent - normal * Vector3.Dot(normal, tangent)).normalized;
                    var hand = Vector3.Dot(Vector3.Cross(authored[a].Normal, authored[a].Tangent), authored[a].Bitangent) < 0f ? -1f : 1f;
                    var bitangent = Vector3.Cross(normal, tangent) * hand;
                    if (vertex.GeometryTriangle is { } geometry)
                    {
                        var sourcePosition = position;
                        a = geometry.A; b = geometry.B; c = geometry.C; bary = vertex.GeometryBary;
                        // Geometry connectivity and deformation use positions. Shading
                        // normal morphs at coincident UV aliases are independent data.
                        var incident = input.Surface.Incident[a];
                        var found = false;
                        if (incident != null)
                            foreach (var item in incident)
                                if (SameTriangle(input.Surface.Triangles[item.Slot], item.Triangle, a, b, c)) { found = true; break; }
                        if (!found)
                            throw new InvalidOperationException("A geometry frame does not name a native source triangle.");
                        position = authored[a].Position * bary.x + authored[b].Position * bary.y + authored[c].Position * bary.z;
                        if ((position - sourcePosition).sqrMagnitude > .000002f * .000002f)
                            throw new InvalidOperationException("A geometry driver does not share its source surface position.");
                        tangent = GeometryUnit(authored[b].Position - authored[a].Position);
                        normal = GeometryUnit(Vector3.Cross(authored[b].Position - authored[a].Position,
                            authored[c].Position - authored[a].Position));
                        bitangent = Vector3.Cross(normal, tangent);
                        hand = 2f;
                    }
                    if (normal.sqrMagnitude < .99f || tangent.sqrMagnitude < .99f)
                        throw new InvalidOperationException("A native source-UV surface has a singular interpolated rest frame.");
                    var primary = source[vertex.Anchor];
                    if (vertex.TangentW is not (-1f or 1f))
                        throw new InvalidOperationException("A fine source tangent has invalid handedness.");
                    Vector3 Coordinates(Vector3 value) => new(Vector3.Dot(value, tangent), Vector3.Dot(value, bitangent), Vector3.Dot(value, normal));
                    points.Add(new NativeSurfacePoint(index, a, b, c, bary,
                        Coordinates(primary.Position + primary.Transfer(vertex.Offset) - position),
                        Coordinates(primary.Transfer(vertex.Normal)), Coordinates(primary.Transfer(vertex.Tangent)), hand, OwnedTangentHand(frameHand, vertex)));
                }
                CheckNativeWork(work);
                var result = new NativeSurfacePlan(points.ToArray(), nativeCount, input.Binding.Vertices.Length,
                    input.Binding.RendererToPrefab);
                CheckNativeWork(work);
                if (input.Bytes + result.Bytes > MaximumNativeWorkBytes) throw new NativeDataPacketLimit();
                if (owner == null) return result;
                Interlocked.CompareExchange(ref owner.NativeSurface, result, null);
                return owner.NativeSurface;

                static bool SameTriangle(int[] indices, int triangle, int a, int b, int c)
                {
                    return Matches(indices[triangle]) && Matches(indices[triangle + 1]) && Matches(indices[triangle + 2]);
                    bool Matches(int value) => value == a || value == b || value == c;
                }
            }

            private sealed class NativeSurfaceInput
            {
                internal Vector3[] Positions, Normals;
                internal Vector2[] Uv;
                internal int[][] Triangles;
                internal NativeSurfaceFrame[] Frames;
                internal List<(int Slot, int Triangle)>[] Incident;
                internal readonly NativeSurfaceAggregate[] FirstEquivalent = new NativeSurfaceAggregate[15], SecondEquivalent = new NativeSurfaceAggregate[15];
            }
            private readonly struct NativeSurfaceFrame(Vector3 position, Vector3 normal, Vector3 tangent, Vector3 bitangent)
            {
                internal readonly Vector3 Position = position, Normal = normal, Tangent = tangent, Bitangent = bitangent;
                internal Vector3 Transfer(Vector3 value) => Tangent * value.x + Bitangent * value.y + Normal * value.z;
                internal Vector3 Coordinates(Vector3 value) => new(Vector3.Dot(value, Tangent), Vector3.Dot(value, Bitangent), Vector3.Dot(value, Normal));
            }
            private readonly struct NativeSurfaceContribution(int anchor, float factor, Vector3 offset, Vector3 normal, Vector3 tangent)
            {
                internal readonly int Anchor = anchor;
                internal readonly float Factor = factor;
                internal readonly Vector3 Offset = offset, Normal = normal, Tangent = tangent;
            }
            private static NativeSurfaceInput CaptureNativeSurface(Mesh nativeMesh, SurfaceBinding binding)
            {
                if (binding.SurfacePlan != null || !binding.Vertices.Any(vertex => !vertex.Native && vertex.Sources == null && vertex.SourceUv.HasValue)) return null;
                return RacesContext.ReadNativeRestMesh(nativeMesh, readable => new NativeSurfaceInput
                {
                    Positions = readable.vertices, Normals = readable.normals, Uv = readable.uv,
                    Triangles = Enumerable.Range(0, readable.subMeshCount).Select(slot => readable.GetTriangles(slot)).ToArray()
                });
            }
            private static void PrepareNativeSurface(NativeChildInput input, NativeDataWork work)
            {
                var surface = input.Surface;
                if (surface == null || surface.Frames != null) return;
                var count = input.Binding.SourceTangents.Length;
                if (surface.Positions.Length != count || surface.Normals.Length != count || surface.Uv.Length != count)
                    throw new InvalidOperationException("A native cloth surface has incomplete rest or source UV channels.");
                var matrix = input.Binding.RendererToPrefab; var normalMatrix = matrix.inverse.transpose;
                var determinant = Vector3.Dot(Vector3.Cross((Vector3)matrix.GetColumn(0), (Vector3)matrix.GetColumn(1)), (Vector3)matrix.GetColumn(2));
                var sign = Mathf.Sign(determinant);
                surface.Frames = new NativeSurfaceFrame[count];
                surface.Incident = new List<(int Slot, int Triangle)>[count];
                for (var index = 0; index < count; index++)
                {
                    if ((index & 255) == 0) CheckNativeWork(work);
                    var normal = normalMatrix.MultiplyVector(surface.Normals[index]).normalized;
                    var tangent = matrix.MultiplyVector((Vector3)input.Binding.SourceTangents[index]);
                    tangent = (tangent - normal * Vector3.Dot(normal, tangent)).normalized;
                    var bitangent = Vector3.Cross(normal, tangent) * input.Binding.SourceTangents[index].w * sign;
                    if (normal.sqrMagnitude < .99f || tangent.sqrMagnitude < .99f || bitangent.sqrMagnitude < .99f)
                        throw new InvalidOperationException("A native cloth surface has a singular rest frame.");
                    surface.Frames[index] = new NativeSurfaceFrame(matrix.MultiplyPoint3x4(surface.Positions[index]), normal, tangent, bitangent);
                    surface.Incident[index] = new List<(int, int)>();
                }
                for (var slot = 0; slot < surface.Triangles.Length; slot++)
                {
                    var triangles = surface.Triangles[slot];
                    if (triangles.Length % 3 != 0) throw new InvalidOperationException("A native cloth surface has invalid triangles.");
                    for (var triangle = 0; triangle < triangles.Length; triangle += 3)
                    {
                        if ((triangle & 255) == 0) CheckNativeWork(work);
                        for (var corner = 0; corner < 3; corner++)
                        {
                            var anchor = triangles[triangle + corner];
                            if (anchor < 0 || anchor >= count) throw new InvalidOperationException("A native cloth surface triangle is out of range.");
                            surface.Incident[anchor].Add((slot, triangle));
                        }
                    }
                }
            }
            private static bool TryDegenerateNativeSurface(NativeSurfaceInput surface, int[] triangles, int offset,
                Vector3 point, Vector2 uv, out Vector3 bary)
            {
                bary = Vector3.zero;
                var a = surface.Frames[triangles[offset]].Position;
                var b = surface.Frames[triangles[offset + 1]].Position;
                var c = surface.Frames[triangles[offset + 2]].Position;
                var ab = b - a; var ac = c - a; var ap = point - a;
                static double Dot(Vector3 first, Vector3 second) =>
                    (double)first.x * second.x + (double)first.y * second.y + (double)first.z * second.z;
                var aa = Dot(ab, ab); var bb = Dot(ac, ac); var cross = Dot(ab, ac);
                var area = aa * bb - cross * cross;
                if (area <= 0d || double.IsNaN(area) || double.IsInfinity(area)) return false;
                var first = surface.Uv[triangles[offset]];
                var second = surface.Uv[triangles[offset + 1]];
                var third = surface.Uv[triangles[offset + 2]];
                var rangeX = Math.Max(first.x, Math.Max(second.x, third.x)) - Math.Min(first.x, Math.Min(second.x, third.x));
                var rangeY = Math.Max(first.y, Math.Max(second.y, third.y)) - Math.Min(first.y, Math.Min(second.y, third.y));
                if (rangeX == 0f && rangeY == 0f)
                {
                    // A constant source UV leaves the physical triangle as the constraint.
                    var u = (bb * Dot(ap, ab) - cross * Dot(ap, ac)) / area;
                    var v = (aa * Dot(ap, ac) - cross * Dot(ap, ab)) / area;
                    if (u >= 0d && v >= 0d && 1d - u - v >= 0d)
                    {
                        bary = new Vector3((float)(1d - u - v), (float)u, (float)v);
                        return true;
                    }
                    var closest = double.PositiveInfinity;
                    for (var edge = 0; edge < 3; edge++)
                    {
                        var next = (edge + 1) % 3;
                        var start = edge == 0 ? a : edge == 1 ? b : c;
                        var end = next == 0 ? a : next == 1 ? b : c;
                        var delta = end - start; var length = Dot(delta, delta);
                        var fraction = length == 0d ? 0d : Math.Max(0d, Math.Min(1d, Dot(point - start, delta) / length));
                        var weights = Vector3.zero; weights[edge] = (float)(1d - fraction); weights[next] = (float)fraction;
                        var residual = point - (a * weights.x + b * weights.y + c * weights.z);
                        var error = Dot(residual, residual);
                        if (error >= closest) continue;
                        closest = error; bary = weights;
                    }
                    return true;
                }

                // A line-valued UV cuts a segment through the physical triangle.
                // Its nearest physical point resolves the remaining degree of freedom.
                var axis = rangeX >= rangeY ? 0 : 1;
                var lower = Math.Min(first[axis], Math.Min(second[axis], third[axis]));
                var upper = Math.Max(first[axis], Math.Max(second[axis], third[axis]));
                var target = Math.Max(lower, Math.Min(upper, uv[axis]));
                var startBary = Vector3.zero; var endBary = Vector3.zero;
                var startPoint = Vector3.zero; var endPoint = Vector3.zero; var contacts = 0;
                void AddContact(Vector3 weights)
                {
                    var position = a * weights.x + b * weights.y + c * weights.z;
                    if (contacts++ == 0) { startBary = endBary = weights; startPoint = endPoint = position; return; }
                    var span = Dot(endPoint - startPoint, endPoint - startPoint);
                    var fromStart = Dot(position - startPoint, position - startPoint);
                    var fromEnd = Dot(position - endPoint, position - endPoint);
                    if (fromStart >= span && fromStart >= fromEnd) { endBary = weights; endPoint = position; }
                    else if (fromEnd >= span) { startBary = weights; startPoint = position; }
                }
                for (var edge = 0; edge < 3; edge++)
                {
                    var next = (edge + 1) % 3;
                    var startUv = edge == 0 ? first : edge == 1 ? second : third;
                    var endUv = next == 0 ? first : next == 1 ? second : third;
                    var start = (double)startUv[axis]; var end = (double)endUv[axis];
                    if (start == end)
                    {
                        if (target != start) continue;
                        var weights = Vector3.zero; weights[edge] = 1f; AddContact(weights);
                        weights = Vector3.zero; weights[next] = 1f; AddContact(weights);
                        continue;
                    }
                    var fraction = (target - start) / (end - start);
                    if (fraction < 0d || fraction > 1d) continue;
                    var contact = Vector3.zero; contact[edge] = (float)(1d - fraction); contact[next] = (float)fraction;
                    AddContact(contact);
                }
                if (contacts == 0) return false;
                var segment = endPoint - startPoint; var segmentLength = Dot(segment, segment);
                var along = segmentLength == 0d ? 0d : Math.Max(0d, Math.Min(1d, Dot(point - startPoint, segment) / segmentLength));
                bary = startBary * (float)(1d - along) + endBary * (float)along;
                return Finite(bary);
            }

            private static bool TryNativeSurfaceCandidate(NativeSurfaceInput surface, SurfaceVertex vertex,
                int[] triangles, int offset, Vector3 point, out Vector3 bary, out Vector3 projected,
                out double distance, out bool interior)
            {
                bary = Vector3.zero; projected = Vector3.zero; distance = double.PositiveInfinity; interior = false;
                var uv = vertex.SourceUv.Value;
                const float uvBound = .000001f;
                var a = triangles[offset]; var b = triangles[offset + 1]; var c = triangles[offset + 2];
                var first = surface.Uv[a]; var second = surface.Uv[b]; var third = surface.Uv[c];
                var ex = (double)second.x - first.x; var ey = (double)second.y - first.y;
                var fx = (double)third.x - first.x; var fy = (double)third.y - first.y;
                var determinant = ex * fy - ey * fx;
                interior = false;
                if (determinant == 0d)
                {
                    if (!TryDegenerateNativeSurface(surface, triangles, offset, point, uv, out bary)) return false;
                }
                else
                {
                    var dx = (double)uv.x - first.x; var dy = (double)uv.y - first.y;
                    var u = (dx * fy - dy * fx) / determinant; var v = (ex * dy - ey * dx) / determinant;
                    interior = u >= 0d && v >= 0d && 1d - u - v >= 0d;
                    bary = new Vector3((float)(1d - u - v), (float)u, (float)v);
                    if (!interior)
                    {
                        // Clamping barycentric components and renormalizing is not
                        // the closest UV point on a narrow source triangle.
                        var closest = double.PositiveInfinity;
                        for (var edge = 0; edge < 3; edge++)
                        {
                            var next = (edge + 1) % 3;
                            var startUv = edge == 0 ? first : edge == 1 ? second : third;
                            var endUv = next == 0 ? first : next == 1 ? second : third;
                            var edgeX = (double)endUv.x - startUv.x;
                            var edgeY = (double)endUv.y - startUv.y;
                            var length = edgeX * edgeX + edgeY * edgeY;
                            var fraction = length == 0d ? 0d : Math.Max(0d, Math.Min(1d,
                                (((double)uv.x - startUv.x) * edgeX + ((double)uv.y - startUv.y) * edgeY) / length));
                            var errorX = startUv.x + edgeX * fraction - uv.x;
                            var errorY = startUv.y + edgeY * fraction - uv.y;
                            var error = errorX * errorX + errorY * errorY;
                            if (error >= closest) continue;
                            closest = error;
                            bary = Vector3.zero;
                            bary[edge] = (float)(1d - fraction);
                            bary[next] = (float)fraction;
                        }
                    }
                }
                var total = bary.x + bary.y + bary.z;
                if (!Finite(total) || total <= 0f) return false;
                bary /= total;
                var projectedUv = first * bary.x + second * bary.y + third * bary.z;
                if (Mathf.Abs(projectedUv.x - uv.x) > uvBound || Mathf.Abs(projectedUv.y - uv.y) > uvBound) return false;
                projected = surface.Frames[a].Position * bary.x + surface.Frames[b].Position * bary.y + surface.Frames[c].Position * bary.z;
                distance = (double)(point - projected).magnitude;
                if (!Finite(projected) || double.IsNaN(distance) || double.IsInfinity(distance))
                    throw new InvalidOperationException("A native cloth source surface is nonfinite.");
                return true;
            }

            private static bool TryCommonNativeSurfaceBoundary(NativeChildInput input, SurfaceVertex vertex,
                int bestTriangle, Vector3 bestBary, Vector3 bestPoint, double bestDistance, bool hasInterior,
                float wireBound, float uvBound, out Vector3 boundaryBary, out Vector3 boundaryPoint)
            {
                boundaryBary = Vector3.zero; boundaryPoint = Vector3.zero;
                var surface = input.Surface; var triangles = surface.Triangles[vertex.SourceSlot];
                var primary = surface.Frames[vertex.Anchor]; var point = primary.Position + primary.Transfer(vertex.Offset);
                var minimum = bestPoint; var maximum = bestPoint; var equivalent = true;
                var common = 0;
                for (var corner = 0; corner < 3; corner++) if (bestBary[corner] > 0f) common |= 1 << corner;
                foreach (var candidate in surface.Incident[vertex.Anchor])
                {
                    if (candidate.Slot != vertex.SourceSlot ||
                        !TryNativeSurfaceCandidate(surface, vertex, triangles, candidate.Triangle, point,
                            out var bary, out var projected, out var distance, out var interior) ||
                        hasInterior && !interior || Math.Abs(distance - bestDistance) > wireBound) continue;
                    minimum = Vector3.Min(minimum, projected); maximum = Vector3.Max(maximum, projected);
                    if (equivalent)
                        foreach (var other in surface.Incident[vertex.Anchor])
                        {
                            if (other.Slot != vertex.SourceSlot ||
                                !TryNativeSurfaceCandidate(surface, vertex, triangles, other.Triangle, point,
                                    out var otherBary, out var otherPoint, out var otherDistance, out var otherInterior) ||
                                hasInterior && !otherInterior || Math.Abs(otherDistance - bestDistance) > wireBound) continue;
                            if ((projected - otherPoint).sqrMagnitude <= wireBound * wireBound &&
                                (SameNativeSurfaceSupport(triangles, candidate.Triangle, bary, other.Triangle, otherBary) ||
                                 EquivalentNativeSurface(input, vertex, triangles, candidate.Triangle, bary, projected,
                                     other.Triangle, otherBary, otherPoint))) continue;
                            equivalent = false;
                            break;
                        }
                    for (var corner = 0; corner < 3; corner++)
                    {
                        if ((common & 1 << corner) == 0) continue;
                        var found = false;
                        for (var other = 0; other < 3; other++)
                            if (bary[other] > 0f && triangles[bestTriangle + corner] == triangles[candidate.Triangle + other]) found = true;
                        if (!found) common &= ~(1 << corner);
                    }
                }
                // A nearer candidate can move an earlier, incompatible point out
                // of the final tie set. Recheck that complete set before narrowing it.
                if (equivalent) { boundaryBary = bestBary; boundaryPoint = bestPoint; return true; }
                // Every tied interpretation must share the same indexed boundary.
                // Keep the intersection of positive supports across the whole fan.
                if (common == 0 || common == 7) return false;
                var first = -1; var second = -1;
                for (var corner = 0; corner < 3; corner++)
                {
                    if ((common & 1 << corner) == 0) continue;
                    var index = triangles[bestTriangle + corner];
                    if (first < 0) first = index; else second = index;
                }
                if (second >= 0 && second < first) (first, second) = (second, first);
                var startUv = surface.Uv[first]; var start = surface.Frames[first].Position;
                var fraction = 0d;
                if (second >= 0)
                {
                    var endUv = surface.Uv[second]; var edgeX = (double)endUv.x - startUv.x; var edgeY = (double)endUv.y - startUv.y;
                    var length = edgeX * edgeX + edgeY * edgeY;
                    if (length > 0d)
                        fraction = Math.Max(0d, Math.Min(1d, (((double)vertex.SourceUv.Value.x - startUv.x) * edgeX +
                            ((double)vertex.SourceUv.Value.y - startUv.y) * edgeY) / length));
                    else
                    {
                        var edge = surface.Frames[second].Position - start; var offset = point - start;
                        var physicalLength = (double)edge.x * edge.x + (double)edge.y * edge.y + (double)edge.z * edge.z;
                        if (physicalLength <= 0d) return false;
                        fraction = Math.Max(0d, Math.Min(1d, ((double)offset.x * edge.x + (double)offset.y * edge.y +
                            (double)offset.z * edge.z) / physicalLength));
                    }
                }
                for (var corner = 0; corner < 3; corner++)
                {
                    var index = triangles[bestTriangle + corner];
                    if (index == first) boundaryBary[corner] = (float)(1d - fraction);
                    else if (index == second) boundaryBary[corner] = (float)fraction;
                }
                var total = boundaryBary.x + boundaryBary.y + boundaryBary.z;
                if (!Finite(total) || total <= 0f) return false;
                boundaryBary /= total;
                var projectedUv = Vector2.zero;
                for (var corner = 0; corner < 3; corner++)
                {
                    var index = triangles[bestTriangle + corner];
                    boundaryPoint += surface.Frames[index].Position * boundaryBary[corner];
                    projectedUv += surface.Uv[index] * boundaryBary[corner];
                }
                if (Mathf.Abs(projectedUv.x - vertex.SourceUv.Value.x) > uvBound ||
                    Mathf.Abs(projectedUv.y - vertex.SourceUv.Value.y) > uvBound) return false;
                // The enclosing box gives an order-independent bound for every
                // prior candidate, including fans with more than two triangles.
                var dx = Math.Max(Math.Abs((double)boundaryPoint.x - minimum.x), Math.Abs((double)boundaryPoint.x - maximum.x));
                var dy = Math.Max(Math.Abs((double)boundaryPoint.y - minimum.y), Math.Abs((double)boundaryPoint.y - maximum.y));
                var dz = Math.Max(Math.Abs((double)boundaryPoint.z - minimum.z), Math.Abs((double)boundaryPoint.z - maximum.z));
                return Finite(boundaryPoint) && dx * dx + dy * dy + dz * dz <= (double)wireBound * wireBound;
            }

            private static int ExplicitNativeSurfaceContributions(NativeChildInput input, SurfaceVertex vertex,
                NativeSurfaceContribution[] output)
            {
                var surface = input.Surface; var triangle = vertex.SurfaceTriangle.Value;
                var bary = vertex.SurfaceBary; var count = input.Binding.SourceTangents.Length;
                var total = bary.x + bary.y + bary.z;
                if (triangle.A < 0 || triangle.A >= count || triangle.B < 0 || triangle.B >= count ||
                    triangle.C < 0 || triangle.C >= count || triangle.A == triangle.B || triangle.A == triangle.C || triangle.B == triangle.C ||
                    vertex.Anchor != triangle.A && vertex.Anchor != triangle.B && vertex.Anchor != triangle.C ||
                    vertex.SourceSlot < 0 || vertex.SourceSlot >= surface.Triangles.Length ||
                    !Finite(bary) || bary.x < 0f || bary.y < 0f || bary.z < 0f ||
                    bary.x > 1f || bary.y > 1f || bary.z > 1f || Mathf.Abs(total - 1f) > .000001f)
                    throw new InvalidOperationException("An explicit native surface stencil is invalid.");
                var source = surface.Triangles[vertex.SourceSlot]; var found = false;
                foreach (var candidate in surface.Incident[vertex.Anchor])
                {
                    if (candidate.Slot != vertex.SourceSlot) continue;
                    var offset = candidate.Triangle;
                    bool Contains(int index) => source[offset] == index || source[offset + 1] == index || source[offset + 2] == index;
                    if (!Contains(triangle.A) || !Contains(triangle.B) || !Contains(triangle.C)) continue;
                    found = true; break;
                }
                if (!found) throw new InvalidOperationException("An explicit native surface triangle is absent from its source slot.");
                bary /= total;
                var uv = surface.Uv[triangle.A] * bary.x + surface.Uv[triangle.B] * bary.y + surface.Uv[triangle.C] * bary.z;
                if (Mathf.Abs(uv.x - vertex.SourceUv.Value.x) > .000001f || Mathf.Abs(uv.y - vertex.SourceUv.Value.y) > .000001f)
                    throw new InvalidOperationException("Explicit native surface provenance disagrees with its source UV.");
                var projected = surface.Frames[triangle.A].Position * bary.x + surface.Frames[triangle.B].Position * bary.y +
                    surface.Frames[triangle.C].Position * bary.z;
                var primary = surface.Frames[vertex.Anchor];
                var residual = primary.Position + primary.Transfer(vertex.Offset) - projected;
                var normal = primary.Transfer(vertex.Normal); var tangent = primary.Transfer(vertex.Tangent); var used = 0;
                if (!Finite(projected) || !Finite(residual) || !Finite(normal) || !Finite(tangent))
                    throw new InvalidOperationException("An explicit native surface frame is not finite.");
                for (var corner = 0; corner < 3; corner++)
                {
                    var factor = bary[corner]; if (factor <= 0f) continue;
                    var anchor = corner == 0 ? triangle.A : corner == 1 ? triangle.B : triangle.C;
                    var frame = surface.Frames[anchor];
                    output[used++] = new NativeSurfaceContribution(anchor, factor, frame.Coordinates(residual),
                        frame.Coordinates(normal), frame.Coordinates(tangent));
                }
                return used;
            }

            private static int NativeSurfaceContributions(NativeChildInput input, SurfaceVertex vertex, NativeSurfaceContribution[] output)
            {
                output[0] = new NativeSurfaceContribution(vertex.Anchor, 1f, vertex.Offset, vertex.Normal, vertex.Tangent);
                var surface = input.Surface;
                if (vertex.SurfaceTriangle.HasValue)
                {
                    if (surface == null || vertex.Native || vertex.Sources != null || !vertex.SourceUv.HasValue)
                        throw new InvalidOperationException("An explicit native surface has no matching source input.");
                    return ExplicitNativeSurfaceContributions(input, vertex, output);
                }
                if (surface == null || vertex.Native || vertex.Sources != null || !vertex.SourceUv.HasValue) return 1;
                var primary = surface.Frames[vertex.Anchor]; var point = primary.Position + primary.Transfer(vertex.Offset);
                var bestDistance = double.PositiveInfinity; var bestPoint = Vector3.zero; var bestBary = Vector3.zero;
                var bestTriangle = -1; var ambiguous = false; var nearby = false; var hasInterior = false; var uv = vertex.SourceUv.Value;
                const float wireBound = .000002f, uvBound = .000001f;
                foreach (var candidate in surface.Incident[vertex.Anchor])
                {
                    if (candidate.Slot != vertex.SourceSlot) continue;
                    var triangles = surface.Triangles[candidate.Slot]; var offset = candidate.Triangle;
                    if (!TryNativeSurfaceCandidate(surface, vertex, triangles, offset, point,
                            out var bary, out var projected, out var distance, out var interior) || hasInterior && !interior) continue;
                    // An actual source-UV interior owns the sample before a clamped neighbouring edge.
                    if (interior && !hasInterior) { hasInterior = true; bestDistance = double.PositiveInfinity; ambiguous = false; nearby = false; }
                    if (distance < bestDistance - wireBound) { ambiguous = false; nearby = false; }
                    else if (Math.Abs(distance - bestDistance) <= wireBound)
                    {
                        nearby = true;
                        if ((projected - bestPoint).sqrMagnitude > wireBound * wireBound ||
                            !SameNativeSurfaceSupport(triangles, offset, bary, bestTriangle, bestBary) &&
                            !EquivalentNativeSurface(input, vertex, triangles, offset, bary, projected, bestTriangle, bestBary, bestPoint)) ambiguous = true;
                    }
                    if (distance >= bestDistance) continue;
                    bestDistance = distance; bestPoint = projected; bestBary = bary; bestTriangle = offset;
                }
                if (nearby)
                {
                    ambiguous = !TryCommonNativeSurfaceBoundary(input, vertex, bestTriangle, bestBary, bestPoint,
                        bestDistance, hasInterior, wireBound, uvBound, out var boundaryBary, out var boundaryPoint);
                    if (!ambiguous)
                    {
                        bestBary = boundaryBary; bestPoint = boundaryPoint;
                    }
                }
                if (bestTriangle < 0 || ambiguous)
                    throw new InvalidOperationException($"A cloth surface attachment has no unambiguous native source UV triangle " +
                        $"(source={input.SurfaceOwner?.Fingerprint}, anchor={vertex.Anchor}, slot={vertex.SourceSlot}, " +
                        $"uv={uv.x:R},{uv.y:R}, triangle={bestTriangle}, ambiguous={ambiguous}).");
                var indices = surface.Triangles[vertex.SourceSlot]; var residual = point - bestPoint;
                var normal = primary.Transfer(vertex.Normal); var tangent = primary.Transfer(vertex.Tangent); var used = 0;
                for (var corner = 0; corner < 3; corner++)
                {
                    var factor = bestBary[corner]; if (factor <= 0f) continue;
                    var anchor = indices[bestTriangle + corner]; var frame = surface.Frames[anchor];
                    output[used++] = new NativeSurfaceContribution(anchor, factor, frame.Coordinates(residual), frame.Coordinates(normal), frame.Coordinates(tangent));
                }
                return used;
            }
            private static bool SameNativeSurfaceSupport(int[] triangles, int first, Vector3 firstBary,
                int second, Vector3 secondBary)
            {
                // Coincident rest points can belong to different moving cloth
                // surfaces. Only the same native interpolation stencil is a tie.
                const float weightBound = .000001f;
                for (var direction = 0; direction < 2; direction++)
                {
                    var a = direction == 0 ? first : second; var b = direction == 0 ? second : first;
                    var wa = direction == 0 ? firstBary : secondBary; var wb = direction == 0 ? secondBary : firstBary;
                    for (var corner = 0; corner < 3; corner++)
                    {
                        var weight = 0f;
                        for (var other = 0; other < 3; other++)
                            if (triangles[a + corner] == triangles[b + other]) weight += wb[other];
                        if (Mathf.Abs(wa[corner] - weight) > weightBound) return false;
                    }
                }
                return true;
            }
            private static float NativeSurfaceMetric(NativeChildInput input)
            {
                var matrix = input.Binding.RendererToPrefab; var scale = input.Scale;
                var lengths = new Vector3(((Vector3)matrix.GetColumn(0)).magnitude,
                    ((Vector3)matrix.GetColumn(1)).magnitude, ((Vector3)matrix.GetColumn(2)).magnitude);
                var ratios = new Vector3(Mathf.Abs(scale.x) / lengths.x, Mathf.Abs(scale.y) / lengths.y, Mathf.Abs(scale.z) / lengths.z);
                var metric = (ratios.x + ratios.y + ratios.z) / 3f;
                if (!Finite(metric) || metric <= 0f ||
                    Mathf.Max(Mathf.Abs(ratios.x - metric), Mathf.Max(Mathf.Abs(ratios.y - metric), Mathf.Abs(ratios.z - metric))) > metric * .00001f)
                    throw new InvalidOperationException("Native and authored source binding metric scales disagree.");
                var determinant = Vector3.Dot(Vector3.Cross((Vector3)matrix.GetColumn(0), (Vector3)matrix.GetColumn(1)), (Vector3)matrix.GetColumn(2));
                if (!Finite(determinant) || determinant == 0f) throw new InvalidOperationException("A native binding has a singular source frame.");
                return metric;
            }
            private static NativeWeightChannels NativeSurfaceWeight(NativeChildInput input, NativeSurfaceContribution contribution,
                NativeWeightChannels source, float metric)
            {
                var normal = source.Normal.normalized; var tangent = source.Tangent;
                tangent = (tangent - normal * Vector3.Dot(normal, tangent)).normalized;
                var hand = input.Binding.SourceTangents[contribution.Anchor].w;
                if (hand is not (-1f or 1f) || normal.sqrMagnitude < .99f || tangent.sqrMagnitude < .99f)
                    throw new InvalidOperationException("A native parent binding has a singular surface frame.");
                var bitangent = Vector3.Cross(normal, tangent) * hand;
                Vector3 Transfer(Vector3 value) => tangent * value.x + bitangent * value.y + normal * value.z;
                if (!contribution.Offset.Equals(Vector3.zero)) source.Position += Transfer(contribution.Offset) * metric;
                source.Factor *= contribution.Factor;
                source.Normal = Transfer(contribution.Normal).normalized; source.Tangent = Transfer(contribution.Tangent).normalized;
                return source;
            }
            private static bool EquivalentNativeSurface(NativeChildInput input, SurfaceVertex vertex, int[] triangles,
                int first, Vector3 firstBary, Vector3 firstPoint, int second, Vector3 secondBary, Vector3 secondPoint)
            {
                if (input.ReadWeight == null) return false;
                var surface = input.Surface; var primary = surface.Frames[vertex.Anchor];
                var point = primary.Position + primary.Transfer(vertex.Offset);
                var normal = primary.Transfer(vertex.Normal); var tangent = primary.Transfer(vertex.Tangent);
                var firstCount = 0; var secondCount = 0; var metric = NativeSurfaceMetric(input);
                for (var direction = 0; direction < 2; direction++)
                {
                    var offset = direction == 0 ? first : second; var bary = direction == 0 ? firstBary : secondBary;
                    var projected = direction == 0 ? firstPoint : secondPoint;
                    var values = direction == 0 ? surface.FirstEquivalent : surface.SecondEquivalent; var used = 0;
                    for (var corner = 0; corner < 3; corner++)
                    {
                        var factor = bary[corner]; if (factor <= 0f) continue;
                        var anchor = triangles[offset + corner]; var frame = surface.Frames[anchor];
                        var contribution = new NativeSurfaceContribution(anchor, factor, frame.Coordinates(point - projected), frame.Coordinates(normal), frame.Coordinates(tangent));
                        var packed = input.Info[anchor]; var count = (int)(packed >> 28); var start = (int)(packed & 0xfffffff);
                        if (start > input.Weights.Length - count) throw new InvalidOperationException("A native source weight span is out of range.");
                        for (var index = 0; index < count; index++)
                            AccumulateNativeSurfaceWeight(values, ref used, NativeSurfaceWeight(input, contribution, input.ReadWeight(start + index), metric));
                    }
                    if (direction == 0) firstCount = used; else secondCount = used;
                }
                for (var direction = 0; direction < 2; direction++)
                {
                    var values = direction == 0 ? surface.FirstEquivalent : surface.SecondEquivalent;
                    var others = direction == 0 ? surface.SecondEquivalent : surface.FirstEquivalent;
                    var count = direction == 0 ? firstCount : secondCount; var otherCount = direction == 0 ? secondCount : firstCount;
                    for (var index = 0; index < count; index++)
                    {
                        var a = values[index].Sum; var found = 0;
                        while (found < otherCount && others[found].Sum.Parent != a.Parent) found++;
                        var b = found == otherCount ? default : others[found].Sum;
                        if (!Same(a.Factor, b.Factor) || !SameVector(a.Position, b.Position) || !SameVector(a.Normal, b.Normal) || !SameVector(a.Tangent, b.Tangent)) return false;
                    }
                }
                return true;
                static bool Same(float a, float b) => Finite(a) && Finite(b) && Mathf.Abs(a - b) <= .000002f * Mathf.Max(1f, Mathf.Abs(a));
                static bool SameVector(Vector3 a, Vector3 b) => Same(a.x, b.x) && Same(a.y, b.y) && Same(a.z, b.z);
            }
            private struct NativeSurfaceAggregate
            {
                internal NativeWeightChannels Sum;
            }
            private static void AccumulateNativeSurfaceWeight(NativeSurfaceAggregate[] values, ref int used,
                NativeWeightChannels value)
            {
                var index = 0;
                while (index < used && values[index].Sum.Parent != value.Parent) index++;
                if (index == used)
                {
                    if (used == 15) throw new InvalidOperationException("A cloth surface exceeds the native packed parent count.");
                    values[used++] = new NativeSurfaceAggregate
                    {
                        Sum = new NativeWeightChannels(0f, value.Parent, Vector3.zero, Vector3.zero, Vector3.zero)
                    };
                }
                ref var aggregate = ref values[index];
                aggregate.Sum.Factor += value.Factor;
                aggregate.Sum.Position += value.Position * value.Factor;
                aggregate.Sum.Normal += value.Normal * value.Factor;
                aggregate.Sum.Tangent += value.Tangent * value.Factor;
            }



            private sealed class NativeChildInput
            {
                internal uint[] Info;
                internal Array Weights;
                internal int[] Parents;
                internal Type OutputType, WeightType;
                internal NativeChildBinding Binding;
                internal NativeSurfaceInput Surface;
                internal SurfaceBinding SurfaceOwner;
                internal bool PlanOnly;
                internal Func<int, NativeWeightChannels> ReadWeight;
                internal Vector3[] Normals;
                internal Vector3 Scale;
                internal int ParentCount;
                internal long Bytes;
            }
            private static object RebuildChildData(object original, SurfaceBinding binding, Mesh nativeMesh,
                Vector3 nativeScale, int parentCount)
            {
                var weightType = ((Array)Get(original, "vertexWeightList")).GetType().GetElementType();
                var rebuild = NativeChildDataBuilder(weightType);
                if (rebuild == null) return RebuildChildDataReflected(original, binding, nativeMesh, nativeScale, parentCount);
                return rebuild(CaptureNativeChild(original, binding, nativeMesh, nativeScale, parentCount, false), null);
            }
            internal static void PrepareNativeMetadata()
            {
                var dataType = Type.GetType("MagicaCloth.MeshData, MagicaCloth", false);
                _nativeWeightType = dataType?.GetField("vertexWeightList", Fields)?.FieldType.GetElementType();
                _nativeChildType = dataType?.GetField("childDataList", Fields)?.FieldType.GetGenericArguments().SingleOrDefault();
                if (_nativeWeightType != null) _ = NativeChildDataBuilder(_nativeWeightType);
                if (dataType != null)
                {
                    PrepareNativeDataWriter(dataType, new HashSet<Type>());
                    PrepareNativeDataWriter(typeof(object[]), new HashSet<Type>());
                    _ = NativeVectorHash(dataType.Assembly);
                }
            }
            private static Func<NativeChildInput, NativeDataWork, object> NativeChildDataBuilder(Type weightType)
            {
                lock (NativeChildDataBuilders)
                {
                    if (NativeChildDataBuilders.TryGetValue(weightType, out var existing)) return existing;
                    var schema = new[] { ("weight", typeof(float)), ("parentIndex", typeof(int)),
                        ("localPos", typeof(Vector3)), ("localNor", typeof(Vector3)), ("localTan", typeof(Vector3)) };
                    Func<NativeChildInput, NativeDataWork, object> rebuild = null;
                    if (weightType.IsValueType && schema.All(member => weightType.GetField(member.Item1, Fields) is { IsInitOnly: false } field && field.FieldType == member.Item2))
                    {
                        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(NativeWeightAccess<>).MakeGenericType(weightType).TypeHandle);
                        var input = Expression.Parameter(typeof(NativeChildInput));
                        var work = Expression.Parameter(typeof(NativeDataWork));
                        var method = typeof(SourceGraph).GetMethod(nameof(RebuildChildDataTyped), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(weightType);
                        rebuild = Expression.Lambda<Func<NativeChildInput, NativeDataWork, object>>(Expression.Call(method, input, work), input, work).Compile();
                    }
                    NativeChildDataBuilders.Add(weightType, rebuild);
                    return rebuild;
                }
            }
            private static NativeChildInput CaptureNativeChild(object original, SurfaceBinding binding, Mesh nativeMesh,
                Vector3 scale, int parentCount, bool copy)
            {
                var info = (uint[])Get(original, "vertexInfoList");
                var weights = (Array)Get(original, "vertexWeightList");
                var parents = (int[])Get(original, "parentIndexList");
                var weightType = weights.GetType().GetElementType();
                var surface = CaptureNativeSurface(nativeMesh, binding);
                long bytes = 0;
                if (copy || surface != null)
                {
                    bytes = 4096L + info.LongLength * 4L + parents.LongLength * 4L + weights.LongLength * Marshal.SizeOf(weightType) +
                            binding.VertexBytes + binding.Vertices.LongLength * 8L + binding.SourceTangents.LongLength * 16L;
                    long count = 0;
                    foreach (var vertex in binding.Vertices)
                    {
                        if (vertex.Anchor < 0 || vertex.Anchor >= info.Length)
                            throw new InvalidOperationException("An authored source attachment references a missing native vertex.");
                        count += info[vertex.Anchor] >> 28;
                    }
                    if (surface == null) bytes += count * Marshal.SizeOf(weightType) * 3L;
                    else bytes += binding.Vertices.LongLength * 8L + surface.Positions.LongLength * 192L + surface.Triangles.Sum(triangles => triangles.LongLength * 20L);
                    if (binding.BoundaryOverrides != null) bytes += binding.SourceTangents.LongLength * 12L + binding.BoundaryOverrides.Count * 128L;
                    if (bytes > MaximumNativeWorkBytes) throw new NativeDataPacketLimit();
                }
                return new NativeChildInput
                {
                    Info = copy ? (uint[])info.Clone() : info, Weights = copy ? (Array)weights.Clone() : weights,
                    Parents = copy ? (int[])parents.Clone() : parents, OutputType = original.GetType(), WeightType = weightType,
                    Binding = new NativeChildBinding(binding, copy), Surface = surface, SurfaceOwner = binding.SurfacePlanSource, Normals = binding.BoundaryOverrides == null ? null : nativeMesh.normals,
                    Scale = scale, ParentCount = parentCount, Bytes = bytes
                };
            }
            private struct NativeWeightChannels
            {
                public float Factor;
                public int Parent;
                public Vector3 Position, Normal, Tangent;
                public NativeWeightChannels(float factor, int parent, Vector3 position, Vector3 normal, Vector3 tangent)
                { Factor = factor; Parent = parent; Position = position; Normal = normal; Tangent = tangent; }
            }
            private static class NativeWeightAccess<T>
            {
                internal static readonly Func<T,NativeWeightChannels> Read;
                internal static readonly Func<T,NativeWeightChannels,T> Write;
                static NativeWeightAccess()
                {
                    if(!typeof(T).IsValueType)throw new InvalidOperationException("A native weight is not a value type.");
                    var weight=Expression.Parameter(typeof(T),"weight");var channels=Expression.Parameter(typeof(NativeWeightChannels),"channels");
                    var mappings=new[]{new[]{"weight","Factor"},new[]{"parentIndex","Parent"},new[]{"localPos","Position"},new[]{"localNor","Normal"},new[]{"localTan","Tangent"}};
                    var fields=mappings.Select(mapping=>new{Native=typeof(T).GetField(mapping[0],Fields),Target=typeof(NativeWeightChannels).GetField(mapping[1])}).ToArray();
                    if(fields.Any(field=>field.Native==null||field.Native.IsInitOnly||field.Native.FieldType!=field.Target.FieldType))throw new InvalidOperationException("The native weight schema has unsupported fields.");
                    Read=Expression.Lambda<Func<T,NativeWeightChannels>>(Expression.New(typeof(NativeWeightChannels).GetConstructor(new[] { typeof(float), typeof(int), typeof(Vector3), typeof(Vector3), typeof(Vector3) }),fields.Select(field=>Expression.Field(weight,field.Native))),weight).Compile();
                    var output=Expression.Variable(typeof(T),"output");var writes=new List<Expression>{Expression.Assign(output,weight)};
                    foreach(var field in fields.Where(field => field.Target.Name != nameof(NativeWeightChannels.Parent)))writes.Add(Expression.Assign(Expression.Field(output,field.Native),Expression.Field(channels,field.Target)));
                    writes.Add(output);Write=Expression.Lambda<Func<T,NativeWeightChannels,T>>(Expression.Block(new[]{output},writes),weight,channels).Compile();
                }
            }

            private static object RebuildChildDataTyped<T>(NativeChildInput input, NativeDataWork work)
            {
                var binding = input.Binding;
                var nativeScale = input.Scale;
                var parentCount = input.ParentCount;
                var sourceInfo = input.Info;
                var sourceWeights = (T[])input.Weights;
                input.ReadWeight = index => NativeWeightAccess<T>.Read(sourceWeights[index]);
                var plan = PrepareNativeSurfacePlan(input, work);
                if (input.PlanOnly) return plan;
                var sourceParents = input.Parents;
                var info = new uint[binding.Vertices.Length];
                var parents = new int[info.Length];
                var weights = new List<T>(info.Length);
                var nativeNormals = input.Normals;
                var matrix = binding.RendererToPrefab;
                var lengths = new Vector3(((Vector3)matrix.GetColumn(0)).magnitude,
                    ((Vector3)matrix.GetColumn(1)).magnitude, ((Vector3)matrix.GetColumn(2)).magnitude);
                var ratios = new Vector3(Mathf.Abs(nativeScale.x) / lengths.x,
                    Mathf.Abs(nativeScale.y) / lengths.y, Mathf.Abs(nativeScale.z) / lengths.z);
                var metric = (ratios.x + ratios.y + ratios.z) / 3f;
                if (!Finite(metric) || metric <= 0f ||
                    Mathf.Max(Mathf.Abs(ratios.x - metric), Mathf.Max(Mathf.Abs(ratios.y - metric), Mathf.Abs(ratios.z - metric))) > metric * 0.00001f)
                    throw new InvalidOperationException("Native and authored source binding metric scales disagree.");
                var determinant = Vector3.Dot(Vector3.Cross((Vector3)matrix.GetColumn(0), (Vector3)matrix.GetColumn(1)),
                    (Vector3)matrix.GetColumn(2));
                if (!Finite(determinant) || determinant == 0f) throw new InvalidOperationException("A native binding has a singular source frame.");
                for (var index = 0; index < info.Length; index++)
                {
                    if ((index & 255) == 0) CheckNativeWork(work);
                    var vertex = binding.Vertices[index];
                    if (vertex.Anchor < 0 || vertex.Anchor >= sourceInfo.Length || vertex.Anchor >= sourceParents.Length)
                        throw new InvalidOperationException("An authored source attachment references a missing native vertex.");
                    var packed = sourceInfo[vertex.Anchor];
                    var count = (int)(packed >> 28); var start = (int)(packed & 0x0fffffff);
                    if (start > sourceWeights.Length - count || weights.Count > 0x0fffffff)
                        throw new InvalidOperationException("A native source weight span is out of range.");
                    info[index] = (uint)(count << 28) | (uint)weights.Count;
                    parents[index] = sourceParents[vertex.Anchor];
                    for (var offset = 0; offset < count; offset++)
                    {
                        var weight = sourceWeights[start + offset];
                        var channels = NativeWeightAccess<T>.Read(weight);
                        var factor = channels.Factor;
                        var parent = channels.Parent;
                        var position = channels.Position;
                        var sourceLocalNormal = channels.Normal;
                        var sourceLocalTangent = channels.Tangent;
                        if (!Finite(factor) || factor < 0f || factor > 1f || parent < 0 || parent >= parentCount ||
                            !Finite(position) || !Finite(sourceLocalNormal) || !Finite(sourceLocalTangent))
                            throw new InvalidOperationException("A native source binding has an invalid parent or nonfinite channel.");
                        var hasBoundary = binding.BoundaryOverrides != null && binding.BoundaryOverrides.TryGetValue(index, out _);
                        if (!vertex.Native || hasBoundary)
                        {
                            var normal = sourceLocalNormal.normalized;
                            var tangent = sourceLocalTangent;
                            tangent = (tangent - normal * Vector3.Dot(normal, tangent)).normalized;
                            var handedness = binding.SourceTangents[vertex.Anchor].w;
                            if (handedness is not (-1f or 1f)) throw new InvalidOperationException("A native source tangent has invalid handedness.");
                            var bitangent = Vector3.Cross(normal, tangent) * handedness;
                            if (normal.sqrMagnitude < 0.99f || tangent.sqrMagnitude < 0.99f)
                                throw new InvalidOperationException("A native parent binding has a singular surface frame.");
                            Vector3 Transfer(Vector3 value) => tangent * value.x + bitangent * value.y + normal * value.z;
                            if (!vertex.Offset.Equals(Vector3.zero))
                                channels.Position = position + Transfer(vertex.Offset) * metric;
                            var surfaceNormal = vertex.Normal; var surfaceTangent = vertex.Tangent;
                            if (hasBoundary)
                            {
                                if (nativeNormals.Length != binding.SourceTangents.Length)
                                    throw new InvalidOperationException("A boundary cloth source has incomplete native normal frames.");
                                var declaration = binding.BoundaryOverrides[index];
                                var sourceNormal = nativeNormals[vertex.Anchor].normalized;
                                var sourceTangent = (Vector3)binding.SourceTangents[vertex.Anchor];
                                sourceTangent = (sourceTangent - sourceNormal * Vector3.Dot(sourceNormal, sourceTangent)).normalized;
                                if (sourceNormal.sqrMagnitude < 0.99f || sourceTangent.sqrMagnitude < 0.99f)
                                    throw new InvalidOperationException("A boundary cloth source has a singular native surface frame.");
                                var sourceBitangent = Vector3.Cross(sourceNormal, sourceTangent) * handedness;
                                Vector3 Coordinates(Vector3 value) => new(Vector3.Dot(value, sourceTangent),
                                    Vector3.Dot(value, sourceBitangent), Vector3.Dot(value, sourceNormal));
                                surfaceNormal = Coordinates(declaration.Normal);
                                surfaceTangent = Coordinates((Vector3)declaration.Tangent);
                            }
                            channels.Normal = Transfer(surfaceNormal).normalized;
                            channels.Tangent = Transfer(surfaceTangent).normalized;
                            weight = NativeWeightAccess<T>.Write(weight, channels);
                        }
                        weights.Add(weight);
                    }
                }
                var result = Activator.CreateInstance(input.OutputType);
                var typedWeights = weights.ToArray();
                Put(result, "vertexCount", info.Length); Put(result, "vertexInfoList", info);
                Put(result, "vertexWeightList", typedWeights); Put(result, "parentIndexList", parents);
                return result;
            }


            private static object RebuildChildDataReflected(object original, SurfaceBinding binding, Mesh nativeMesh,
                Vector3 nativeScale, int parentCount)
            {
                var input = CaptureNativeChild(original, binding, nativeMesh, nativeScale, parentCount, false);
                var sourceInfo = (uint[])Get(original, "vertexInfoList");
                var sourceWeights = (Array)Get(original, "vertexWeightList");
                var sourceParents = (int[])Get(original, "parentIndexList");
                var weightType = sourceWeights.GetType().GetElementType();
                var readFactor = NativeReader(weightType, "weight");
                var readParent = NativeReader(weightType, "parentIndex");
                var readPosition = NativeReader(weightType, "localPos");
                var readNormal = NativeReader(weightType, "localNor");
                var readTangent = NativeReader(weightType, "localTan");
                input.ReadWeight = index => { var value = sourceWeights.GetValue(index); return new NativeWeightChannels((float)readFactor(value), (int)readParent(value), (Vector3)readPosition(value), (Vector3)readNormal(value), (Vector3)readTangent(value)); };
                _ = PrepareNativeSurfacePlan(input, null);
                var info = new uint[binding.Vertices.Length];
                var parents = new int[info.Length];
                var weights = new List<object>();
                var nativeNormals = binding.BoundaryOverrides == null ? null : nativeMesh.normals;
                var matrix = binding.RendererToPrefab;
                var lengths = new Vector3(((Vector3)matrix.GetColumn(0)).magnitude,
                    ((Vector3)matrix.GetColumn(1)).magnitude, ((Vector3)matrix.GetColumn(2)).magnitude);
                var ratios = new Vector3(Mathf.Abs(nativeScale.x) / lengths.x,
                    Mathf.Abs(nativeScale.y) / lengths.y, Mathf.Abs(nativeScale.z) / lengths.z);
                var metric = (ratios.x + ratios.y + ratios.z) / 3f;
                if (!Finite(metric) || metric <= 0f ||
                    Mathf.Max(Mathf.Abs(ratios.x - metric), Mathf.Max(Mathf.Abs(ratios.y - metric), Mathf.Abs(ratios.z - metric))) > metric * 0.00001f)
                    throw new InvalidOperationException("Native and authored source binding metric scales disagree.");
                var determinant = Vector3.Dot(Vector3.Cross((Vector3)matrix.GetColumn(0), (Vector3)matrix.GetColumn(1)),
                    (Vector3)matrix.GetColumn(2));
                if (!Finite(determinant) || determinant == 0f) throw new InvalidOperationException("A native binding has a singular source frame.");
                for (var index = 0; index < info.Length; index++)
                {
                    var vertex = binding.Vertices[index];
                    if (vertex.Anchor < 0 || vertex.Anchor >= sourceInfo.Length || vertex.Anchor >= sourceParents.Length)
                        throw new InvalidOperationException("An authored source attachment references a missing native vertex.");
                    var packed = sourceInfo[vertex.Anchor];
                    var count = (int)(packed >> 28); var start = (int)(packed & 0x0fffffff);
                    if (start > sourceWeights.Length - count || weights.Count > 0x0fffffff)
                        throw new InvalidOperationException("A native source weight span is out of range.");
                    info[index] = (uint)(count << 28) | (uint)weights.Count;
                    parents[index] = sourceParents[vertex.Anchor];
                    for (var offset = 0; offset < count; offset++)
                    {
                        var weight = sourceWeights.GetValue(start + offset);
                        var factor = (float)readFactor(weight);
                        var parent = (int)readParent(weight);
                        var position = (Vector3)readPosition(weight);
                        var sourceLocalNormal = (Vector3)readNormal(weight);
                        var sourceLocalTangent = (Vector3)readTangent(weight);
                        if (!Finite(factor) || factor < 0f || factor > 1f || parent < 0 || parent >= parentCount ||
                            !Finite(position) || !Finite(sourceLocalNormal) || !Finite(sourceLocalTangent))
                            throw new InvalidOperationException("A native source binding has an invalid parent or nonfinite channel.");
                        var hasBoundary = binding.BoundaryOverrides != null && binding.BoundaryOverrides.TryGetValue(index, out _);
                        if (!vertex.Native || hasBoundary)
                        {
                            var normal = sourceLocalNormal.normalized;
                            var tangent = sourceLocalTangent;
                            tangent = (tangent - normal * Vector3.Dot(normal, tangent)).normalized;
                            var handedness = binding.SourceTangents[vertex.Anchor].w;
                            if (handedness is not (-1f or 1f)) throw new InvalidOperationException("A native source tangent has invalid handedness.");
                            var bitangent = Vector3.Cross(normal, tangent) * handedness;
                            if (normal.sqrMagnitude < 0.99f || tangent.sqrMagnitude < 0.99f)
                                throw new InvalidOperationException("A native parent binding has a singular surface frame.");
                            Vector3 Transfer(Vector3 value) => tangent * value.x + bitangent * value.y + normal * value.z;
                            if (!vertex.Offset.Equals(Vector3.zero))
                                Put(weight, "localPos", position + Transfer(vertex.Offset) * metric);
                            var surfaceNormal = vertex.Normal; var surfaceTangent = vertex.Tangent;
                            if (hasBoundary)
                            {
                                if (nativeNormals.Length != binding.SourceTangents.Length)
                                    throw new InvalidOperationException("A boundary cloth source has incomplete native normal frames.");
                                var declaration = binding.BoundaryOverrides[index];
                                var sourceNormal = nativeNormals[vertex.Anchor].normalized;
                                var sourceTangent = (Vector3)binding.SourceTangents[vertex.Anchor];
                                sourceTangent = (sourceTangent - sourceNormal * Vector3.Dot(sourceNormal, sourceTangent)).normalized;
                                if (sourceNormal.sqrMagnitude < 0.99f || sourceTangent.sqrMagnitude < 0.99f)
                                    throw new InvalidOperationException("A boundary cloth source has a singular native surface frame.");
                                var sourceBitangent = Vector3.Cross(sourceNormal, sourceTangent) * handedness;
                                Vector3 Coordinates(Vector3 value) => new(Vector3.Dot(value, sourceTangent),
                                    Vector3.Dot(value, sourceBitangent), Vector3.Dot(value, sourceNormal));
                                surfaceNormal = Coordinates(declaration.Normal);
                                surfaceTangent = Coordinates((Vector3)declaration.Tangent);
                            }
                            Put(weight, "localNor", Transfer(surfaceNormal).normalized);
                            Put(weight, "localTan", Transfer(surfaceTangent).normalized);
                        }
                        weights.Add(weight);
                    }
                }
                var result = Activator.CreateInstance(original.GetType());
                var typedWeights = Array.CreateInstance(sourceWeights.GetType().GetElementType(), weights.Count);
                for (var index = 0; index < weights.Count; index++) typedWeights.SetValue(weights[index], index);
                Put(result, "vertexCount", info.Length); Put(result, "vertexInfoList", info);
                Put(result, "vertexWeightList", typedWeights); Put(result, "parentIndexList", parents);
                return result;
            }

            private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
            private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
            private static UnityEngine.Object CreateNativeData(UnityEngine.Object original)
            {
                var result = ScriptableObject.CreateInstance(original.GetType());
                try
                {
                    // Native runtime initialization only reads these immutable channels. Replaced child arrays are new.
                    foreach (var field in NativeDataFields(original.GetType())) field.SetValue(result, field.GetValue(original));
                    return result;
                }
                catch { UnityEngine.Object.Destroy(result); throw; }
            }
            private static IEnumerable<FieldInfo> NativeDataFields(Type type)
            {
                for (; type != null && type != typeof(UnityEngine.Object); type = type.BaseType)
                    foreach (var field in type.GetFields(Fields | BindingFlags.DeclaredOnly).Where(field => !field.IsStatic &&
                                 !field.IsNotSerialized && (field.IsPublic || field.IsDefined(typeof(SerializeField), false)))
                                 .OrderBy(field => field.Name, StringComparer.Ordinal))
                        yield return field;
            }
            internal static HashSet<UnityEngine.Object> NativePrefabData(GameObject prefab)
            {
                var result = new HashSet<UnityEngine.Object>();
                if (!prefab || prefab.scene.IsValid()) return result;
                foreach (var component in prefab.GetComponentsInChildren<Component>(true))
                {
                    if (!component || !(ClothType?.IsInstanceOfType(component) == true ||
                                        VirtualType?.IsInstanceOfType(component) == true)) continue;
                    var deformer = Optional(component, "Deformer");
                    if (deformer != null && Optional(deformer, "MeshData") is UnityEngine.Object data && data &&
                        data.GetType().Assembly == DeformerType.Assembly) result.Add(data);
                }
                return result;
            }
            private sealed class NativeDataReferences : IEqualityComparer<object>
            {
                bool IEqualityComparer<object>.Equals(object first, object second) => ReferenceEquals(first, second);
                int IEqualityComparer<object>.GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
            }
            internal static long NativeDataMemorySize(UnityEngine.Object data)
            {
                var bytes = Math.Max(0L, UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(data));
                if (data is Texture2D texture)
                {
                    // Unreadable textures can report only their small object allocation.
                    // Budget at least the device-format payload, including every mip tail.
                    var width = texture.width; var height = texture.height;
                    var format = texture.graphicsFormat; var levels = texture.mipmapCount;
                    long payload = 0;
                    for (var level = 0; level < levels; level++)
                    {
                        payload = checked(payload + UnityEngine.Experimental.Rendering.GraphicsFormatUtility.ComputeMipmapSize(width, height, format));
                        width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
                    }
                    bytes = Math.Max(bytes, payload);
                }
                return Math.Max(bytes, data.GetType().Assembly == DeformerType?.Assembly
                    ? SerializedBytes(data, new HashSet<object>(new NativeDataReferences())) : 0);
            }
            private static long SerializedBytes(object value, HashSet<object> seen)
            {
                if (value == null) return 0;
                var type = value.GetType();
                if (type.IsValueType) return Marshal.SizeOf(type);
                if (!seen.Add(value)) return 0;
                if (value is string text) return 24L + 2L * text.Length;
                if (value is Array array)
                {
                    var element = type.GetElementType();
                    if (element.IsValueType) return 24L + (long)array.Length * Marshal.SizeOf(element);
                    var bytes = 24L + (long)array.Length * IntPtr.Size;
                    foreach (var item in array) bytes += SerializedBytes(item, seen);
                    return bytes;
                }
                if (value is IList list)
                {
                    if (type.GetField("_items", Fields) is { } items) return 24L + SerializedBytes(items.GetValue(value), seen);
                    var bytes = 24L + (long)list.Count * IntPtr.Size;
                    foreach (var item in list) bytes += SerializedBytes(item, seen);
                    return bytes;
                }
                if (type.Assembly == DeformerType.Assembly)
                    return 24L + NativeDataFields(type).Sum(field => SerializedBytes(field.GetValue(value), seen));
                throw new InvalidOperationException("Unsupported native source data memory channel: " + type.FullName);
            }
            internal sealed class NativeDataNode
            {
                internal byte[] NativeDataHeader;
                internal object Value;
                internal string[] Fields;
                internal NativeDataNode[] Children;
                internal bool Sequence;
            }
            private sealed class NativeDataSnapshot { internal NativeDataNode Root; internal long Bytes; }
            sealed class NativeDataCapture
            {
                readonly Dictionary<object, object> copies = new Dictionary<object, object>(new NativeDataReferences());
                readonly HashSet<object> path = new HashSet<object>(new NativeDataReferences());
                readonly long maximum;
                long bytes;
                internal NativeDataCapture(long maximum = MaximumNativeWorkBytes) { this.maximum = maximum; }
                void Own(long count) { bytes += count; if (bytes > maximum) throw new NativeDataPacketLimit(); }
                internal NativeDataSnapshot Read(object source) => new NativeDataSnapshot { Root = Copy(source), Bytes = bytes };
                NativeDataNode Copy(object value)
                {
                    Own(128);
                    if (value == null) return new NativeDataNode();
                    var type = value.GetType();
                    if (type.IsValueType && NativeDataPureValue(type)) { Own(Marshal.SizeOf(type.IsEnum ? Enum.GetUnderlyingType(type) : type)); return new NativeDataNode { Value = value }; }
                    if (value is Array array && type == type.GetElementType().MakeArrayType() && NativeDataPureValue(type.GetElementType()))
                    {
                        if (!copies.TryGetValue(value, out var copy))
                        {
                            Own(24L + (long)array.Length * Marshal.SizeOf(type.GetElementType()));
                            copies.Add(value, copy = array.Clone());
                        }
                        if (ReferenceEquals(copy, value)) throw new InvalidOperationException("A packet array aliases the native source.");
                        return new NativeDataNode { Value = copy };
                    }
                    if (!path.Add(value)) throw new InvalidOperationException("A native source snapshot contains a cyclic serialized graph.");
                    try
                    {
                        var node = new NativeDataNode { NativeDataHeader = NativeDataHeader(type) }; Own(node.NativeDataHeader.Length);
                        if (value is IList list)
                        {
                            Own(24L + list.Count * (long)IntPtr.Size);
                            node.Sequence = true; node.Children = new NativeDataNode[list.Count];
                            for (int index = 0; index < list.Count; index++) node.Children[index] = Copy(list[index]);
                        }
                        else if (type.Assembly == DeformerType.Assembly)
                        {
                            var fields = NativeDataFields(type).ToArray();
                            Own(48L + fields.Length * 2L * IntPtr.Size);
                            node.Fields = new string[fields.Length]; node.Children = new NativeDataNode[fields.Length];
                            for (int index = 0; index < fields.Length; index++)
                            {
                                node.Fields[index] = fields[index].DeclaringType.FullName + ":" + fields[index].Name;
                                Own(24L + node.Fields[index].Length * 2L); node.Children[index] = Copy(fields[index].GetValue(value));
                            }
                        }
                        else throw new InvalidOperationException("Unsupported native snapshot channel: " + type.FullName);
                        return node;
                    }
                    finally { path.Remove(value); }
                }
            }
            static bool NativeDataPureValue(Type type) => type.IsValueType && (type.IsPrimitive || type.IsEnum || type.GetFields(Fields).Where(field => !field.IsStatic).All(field => NativeDataPureValue(field.FieldType)));

            static byte[] NativeDataHeader(Type type)
            {
                using (var stream = new MemoryStream())
                { using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) { writer.Write(true); writer.Write(type.FullName); } return stream.ToArray(); }
            }
            sealed class NativeDataHashStream : Stream
            {
                readonly HashAlgorithm algorithm;
                readonly NativeDataWork request;
                internal NativeDataHashStream(HashAlgorithm algorithm, NativeDataWork request) { this.algorithm = algorithm; this.request = request; }
                public override void Write(byte[] buffer, int offset, int count) { CheckNativeWork(request); algorithm.TransformBlock(buffer, offset, count, null, 0); }
                public override void Flush() { CheckNativeWork(request); }
                public override bool CanRead => false;
                public override bool CanSeek => false;
                public override bool CanWrite => true;
                public override long Length => throw new NotSupportedException();
                public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
                public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
                public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
                public override void SetLength(long value) => throw new NotSupportedException();
            }


            private const long MaximumNativeWorkBytes = 32L * 1024 * 1024;
            private static int _nativeDataWorkBusy;
            private sealed class NativeDataPacketLimit : Exception { }
            private sealed class NativeDataWork
            {
                internal NativeDataSnapshot Snapshot;
                internal NativeChildInput Child;
                internal volatile bool Cancelled;
                private int _references = 1;
                internal void RetainWorker()
                {
                    if (Interlocked.CompareExchange(ref _references, 2, 1) != 1)
                        throw new InvalidOperationException("A native data packet has no exclusive preparing owner.");
                }
                internal void Release()
                {
                    var remaining = Interlocked.Decrement(ref _references);
                    if (remaining < 0) throw new InvalidOperationException("A native data packet has no matching owner.");
                    if (remaining == 0) Interlocked.Exchange(ref _nativeDataWorkBusy, 0);
                }
            }
            private static void CheckNativeWork(NativeDataWork request)
            {
                if (request?.Cancelled == true) throw new OperationCanceledException("A native source data request was superseded.");
            }
            private static Task<object> QueueNativeDataWork(NativeDataWork request)
            {
                request.RetainWorker();
                try
                {
                    return Task.Run(() =>
                    {
                        try
                        {
                            CheckNativeWork(request);
                            return request.Snapshot != null ? HashNativeSnapshot(request) :
                                NativeChildDataBuilder(request.Child.WeightType)(request.Child, request);
                        }
                        finally { request.Release(); }
                    });
                }
                catch { request.Release(); throw; }
            }
            private static IEnumerator ExecuteNativeDataWork(Func<NativeDataWork> capture, Func<bool> current, Action<object> receive)
            {
                while (Interlocked.CompareExchange(ref _nativeDataWorkBusy, 1, 0) != 0)
                {
                    if (!current()) yield break;
                    yield return null;
                }
                NativeDataWork request = null;
                try
                {
                    if (!current()) yield break;
                    request = capture();
                    if (request == null) yield break;
                    var job = QueueNativeDataWork(request);
                    while (!job.IsCompleted)
                    {
                        if (!current())
                        {
                            request.Cancelled = true;
                            _ = job.ContinueWith(failed => _ = failed.Exception, TaskContinuationOptions.OnlyOnFaulted);
                            yield break;
                        }
                        yield return null;
                    }
                    var result = job.GetAwaiter().GetResult();
                    if (current()) receive(result);
                }
                finally
                {
                    if (request != null) { request.Cancelled = true; request.Release(); request = null; }
                    else Interlocked.Exchange(ref _nativeDataWorkBusy, 0);
                }
            }
            internal static IEnumerator PrepareNativeFingerprint(UnityEngine.Object source, Func<bool> current, Action<string> receive)
            {
                yield return ExecuteNativeDataWork(() =>
                {
                    try { return new NativeDataWork { Snapshot = new NativeDataCapture().Read(source) }; }
                    catch (NativeDataPacketLimit) { return null; }
                }, current, value => receive((string)value));
            }
            private static IEnumerator PrepareNativeChild(object source, SurfaceBinding binding, Mesh nativeMesh,
                Vector3 scale, int parents, Func<bool> current, Action<object> receive, bool planOnly = false)
            {
                var weightType = ((Array)Get(source, "vertexWeightList")).GetType().GetElementType();
                if (source.GetType() != _nativeChildType || weightType != _nativeWeightType || !NativeDataPureValue(weightType) ||
                    NativeChildDataBuilder(weightType) == null) yield break;
                yield return ExecuteNativeDataWork(() =>
                {
                    try
                    {
                        var input = CaptureNativeChild(source, binding, nativeMesh, scale, parents, true);
                        input.PlanOnly = planOnly;
                        return new NativeDataWork { Child = input };
                    }
                    catch (NativeDataPacketLimit) { return null; }
                }, current, receive);
            }
            private static string HashNativeSnapshot(NativeDataWork request)
            {
                CheckNativeWork(request);
                using var algorithm = CreateAssetSha256();
                using var target = new NativeDataHashStream(algorithm, request);
                using var buffer = new BufferedStream(target, 65536);
                using var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, true);
                WriteNativeSnapshot(writer, request.Snapshot.Root, request);
                writer.Flush(); buffer.Flush(); CheckNativeWork(request);
                algorithm.TransformFinalBlock([], 0, 0);
                return BitConverter.ToString(algorithm.Hash).Replace("-", "");
            }
            private static void WriteNativeSnapshot(BinaryWriter writer, NativeDataNode node, NativeDataWork request)
            {
                CheckNativeWork(request);
                if (node.Value != null) { WriteNativeData(writer, node.Value); return; }
                if (node.NativeDataHeader == null) { writer.Write(false); return; }
                writer.Write(node.NativeDataHeader);
                if (node.Sequence) writer.Write(node.Children.Length);
                for (var index = 0; index < node.Children.Length; index++)
                {
                    if (node.Fields != null) writer.Write(node.Fields[index]);
                    WriteNativeSnapshot(writer, node.Children[index], request);
                }
            }
            private static void PrepareNativeDataWriter(Type type, HashSet<Type> visited)
            {
                if (type == null || !visited.Add(type)) return;
                if (type.IsArray) PrepareNativeDataWriter(type.GetElementType(), visited);
                else if (Nullable.GetUnderlyingType(type) is { } underlying) PrepareNativeDataWriter(underlying, visited);
                else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                    PrepareNativeDataWriter(type.GetGenericArguments()[0], visited);
                else if (type.Assembly == DeformerType.Assembly)
                    foreach (var field in NativeDataFields(type)) PrepareNativeDataWriter(field.FieldType, visited);
                lock (NativeDataWriters)
                    if (!NativeDataWriters.ContainsKey(type)) NativeDataWriters.Add(type, CreateNativeDataWriter(type));
            }

            private static readonly Dictionary<Type, Action<BinaryWriter, object>> NativeDataWriters = new();

            private static string HashNativeData(object data)
            {
                using var hash = CreateAssetSha256();
                using var crypto = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
                using var stream = new BufferedStream(crypto, 65536);
                using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
                WriteNativeData(writer, data); writer.Flush(); stream.Flush(); crypto.FlushFinalBlock();
                return BitConverter.ToString(hash.Hash).Replace("-", "");
            }
            private static void WriteNativeData(BinaryWriter writer, object value)
            {
                if (value == null) { writer.Write(false); return; }
                var type = value.GetType();
                Action<BinaryWriter, object> write;
                lock (NativeDataWriters)
                {
                    if (!NativeDataWriters.TryGetValue(type, out write))
                        NativeDataWriters.Add(type, write = CreateNativeDataWriter(type));
                }
                write(writer, value);
            }
            private static void WriteNativeDataItems(BinaryWriter writer, IList items)
            {
                writer.Write(items.Count);
                foreach (var item in items) WriteNativeData(writer, item);
            }
            private static Action<BinaryWriter, object> CreateNativeDataWriter(Type type)
            {
                var writerParameter = Expression.Parameter(typeof(BinaryWriter), "writer");
                var argument = Expression.Parameter(typeof(object), "value");
                var dispatch = typeof(SourceGraph).GetMethod(nameof(WriteNativeData), BindingFlags.Static | BindingFlags.NonPublic);
                var itemsDispatch = typeof(SourceGraph).GetMethod(nameof(WriteNativeDataItems), BindingFlags.Static | BindingFlags.NonPublic);
                return Expression.Lambda<Action<BinaryWriter, object>>(Build(Expression.Convert(argument, type), true), writerParameter, argument).Compile();
                Expression Build(Expression value, bool runtimeType = false)
                {
                    var type = value.Type;
                    if (Nullable.GetUnderlyingType(type) != null)
                        return Expression.IfThenElse(Expression.Property(value, "HasValue"),
                            Build(Expression.Property(value, "Value")), Call(Expression.Constant(false)));
                    // Reference fields dispatch by actual type, including covariant arrays and derived data.
                    if (!runtimeType && !type.IsValueType)
                        return Expression.Call(dispatch, writerParameter, Expression.Convert(value, typeof(object)));
                    var body = new List<Expression> { CallBytes(Header(type)) };
                    if (type == typeof(bool) || type == typeof(int) || type == typeof(uint) || type == typeof(ulong))
                        body.Add(Call(value));
                    else if (type == typeof(float))
                    {
                        var finite = Expression.AndAlso(Expression.Not(Expression.Call(typeof(float), nameof(float.IsNaN), null, value)),
                            Expression.Not(Expression.Call(typeof(float), nameof(float.IsInfinity), null, value)));
                        body.Add(Expression.IfThen(Expression.Not(finite), Error("A native source binding has a nonfinite scalar.")));
                        body.Add(Call(value));
                    }
                    else if (type == typeof(Vector2) || type == typeof(Vector3))
                    {
                        body.Add(Call(Expression.Field(value, "x"))); body.Add(Call(Expression.Field(value, "y")));
                        if (type == typeof(Vector3)) body.Add(Call(Expression.Field(value, "z")));
                    }
                    else if (type.IsArray && type == type.GetElementType().MakeArrayType() ||
                             type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                    {
                        var index = Expression.Variable(typeof(int), "index");
                        var stop = Expression.Label();
                        var count = type.IsArray ? (Expression)Expression.ArrayLength(value) : Expression.Property(value, "Count");
                        var item = type.IsArray ? (Expression)Expression.ArrayIndex(value, index) : Expression.Property(value, "Item", index);
                        body.Add(Call(count));
                        body.Add(Expression.Block(new[] { index }, Expression.Assign(index, Expression.Constant(0)),
                            Expression.Loop(Expression.IfThenElse(Expression.LessThan(index, count),
                                Expression.Block(Build(item), Expression.PostIncrementAssign(index), Expression.Empty()), Expression.Break(stop)), stop)));
                    }
                    else if (typeof(IList).IsAssignableFrom(type))
                    {
                        body.Add(Expression.Call(itemsDispatch, writerParameter, Expression.Convert(value, typeof(IList))));
                    }
                    else if (type.Assembly == DeformerType.Assembly)
                    {
                        foreach (var field in NativeDataFields(type))
                        {
                            body.Add(CallBytes(Encode(field.DeclaringType.FullName + ":" + field.Name)));
                            body.Add(Build(Expression.Field(value, field)));
                        }
                    }
                    else body.Add(Error("Unsupported native binding fingerprint channel: " + type.FullName));
                    body.Add(Expression.Empty());
                    return Expression.Block(body);
                }
                Expression Call(Expression value) => Expression.Call(writerParameter,
                    typeof(BinaryWriter).GetMethod(nameof(BinaryWriter.Write), new[] { value.Type }), value);
                Expression CallBytes(byte[] value) => Call(Expression.Constant(value));
                Expression Error(string message) => Expression.Throw(Expression.New(
                    typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) }), Expression.Constant(message)));
                byte[] Header(Type type)
                {
                    using var encoded = new MemoryStream();
                    using var text = new BinaryWriter(encoded, System.Text.Encoding.UTF8, true);
                    text.Write(true); text.Write(type.FullName); text.Flush();
                    return encoded.ToArray();
                }
                byte[] Encode(string value)
                {
                    using var encoded = new MemoryStream();
                    using var text = new BinaryWriter(encoded, System.Text.Encoding.UTF8, true);
                    text.Write(value); text.Flush();
                    return encoded.ToArray();
                }
            }

            // The caller completes native jobs and validates prospective shared-data collisions before registration.
            private static void ValidateNativeSharedBindings(object managerMesh, UnityEngine.Object prospectiveVirtualData)
            {
                if (managerMesh == null || !prospectiveVirtualData)
                    throw new ArgumentNullException(nameof(prospectiveVirtualData));
                var uid = (int)Get(prospectiveVirtualData, "SaveDataHash");
                if (uid == 0 || (int)Invoke(prospectiveVirtualData, "GetDataHash") != uid)
                    throw new InvalidOperationException("Prospective native mesh data has no current native verification identity.");
                var virtualIds = (IDictionary)Get(managerMesh, "sharedVirtualMeshIdToIndexDict");
                var childIds = (IDictionary)Get(managerMesh, "sharedChildMeshIdToSharedVirtualMeshIndexDict");
                int? parentIndex = virtualIds.Contains(uid) ? (int)virtualIds[uid] : null;
                if (parentIndex.HasValue)
                {
                    var info = NativeBindingItem(Get(managerMesh, "sharedVirtualMeshInfoList"), parentIndex.Value);
                    if ((int)Get(info, "uid") != uid || (int)Get(info, "useCount") <= 0)
                        throw new InvalidOperationException("The native shared virtual identity has no live matching owner.");
                    MatchNativeBindingChunk(managerMesh, info, "vertexChunk", "sharedVirtualVertexInfoList", (Array)Get(prospectiveVirtualData, "vertexInfoList"));
                    MatchNativeBindingChunk(managerMesh, info, "weightChunk", "sharedVirtualWeightList", (Array)Get(prospectiveVirtualData, "vertexWeightList"));
                    MatchNativeOptionalBindingChunk(managerMesh, info, "vertexChunk", "sharedVirtualUvList", Optional(prospectiveVirtualData, "uvList") as Array);
                    MatchNativeOptionalBindingChunk(managerMesh, info, "triangleChunk", "sharedVirtualTriangleList", Optional(prospectiveVirtualData, "triangleList") as Array);
                    MatchNativeOptionalBindingChunk(managerMesh, info, "vertexChunk", "sharedVirtualVertexToTriangleInfoList", Optional(prospectiveVirtualData, "vertexToTriangleInfoList") as Array);
                    MatchNativeOptionalBindingChunk(managerMesh, info, "vertexToTriangleChunk", "sharedVirtualVertexToTriangleIndexList", Optional(prospectiveVirtualData, "vertexToTriangleIndexList") as Array);
                }
                var children = (IList)Get(prospectiveVirtualData, "childDataList");
                for (var index = 0; index < children.Count; index++)
                {
                    var childUid = unchecked((long)uid << ((16 + index) & 63));
                    if (!childIds.Contains(childUid)) continue;
                    var info = NativeBindingItem(Get(managerMesh, "sharedChildMeshInfoList"), (int)childIds[childUid]);
                    if ((long)Get(info, "cuid") != childUid || (int)Get(info, "meshUseCount") <= 0 ||
                        !parentIndex.HasValue || (int)Get(info, "sharedVirtualMeshIndex") != parentIndex.Value)
                        throw new InvalidOperationException("A shared native child identity belongs to another virtual source.");
                    var child = children[index];
                    MatchNativeBindingChunk(managerMesh, info, "vertexChunk", "sharedChildVertexInfoList", (Array)Get(child, "vertexInfoList"));
                    MatchNativeBindingChunk(managerMesh, info, "weightChunk", "sharedChildWeightList", (Array)Get(child, "vertexWeightList"));
                }
            }
            private static readonly Dictionary<Type, Func<object, int, object>> NativeBindingReaders = new();
            private static readonly Dictionary<(Type Array, Type Container), Func<Array, object, int, int, bool>> NativeBindingMatchers = new();
            private static readonly Dictionary<(Type Expected, Type Actual), Func<object, object, bool>> NativeBindingValues = new();

            private static object NativeBindingItem(object values, int index)
            {
                if (index < 0) throw new InvalidOperationException("A native shared binding references an invalid runtime slot.");
                if (values == null) throw new MissingMemberException(null, "Length");
                var type = values.GetType();
                Func<object, int, object> read;
                lock (NativeBindingReaders)
                {
                    if (!NativeBindingReaders.TryGetValue(type, out read))
                        NativeBindingReaders.Add(type, read = CreateNativeBindingReader(type));
                }
                return read(values, index);
            }
            private static Func<object, int, object> CreateNativeBindingReader(Type type)
            {
                var value = Expression.Parameter(typeof(object), "value");
                var index = Expression.Parameter(typeof(int), "index");
                var container = Expression.Convert(value, type);
                var length = NativeBindingMember(container, "Length");
                var item = type.GetProperty("Item", Fields);
                Expression read = item == null
                    ? NativeBindingError(typeof(object), new MissingMemberException(type.FullName, "Item"))
                    : Expression.Convert(Expression.Property(container, item, index), typeof(object));
                read = Expression.Condition(Expression.OrElse(Expression.LessThan(index, Expression.Constant(0)),
                        Expression.GreaterThanOrEqual(index, length)),
                    NativeBindingError(typeof(object), new InvalidOperationException("A native shared binding references an invalid runtime slot.")), read);
                return Expression.Lambda<Func<object, int, object>>(read, value, index).Compile();
            }
            private static void MatchNativeOptionalBindingChunk(object managerMesh, object info, string chunkName,
                string arrayName, Array expected)
            {
                // Native SetShared* copies these optional inputs only when nonempty.
                if (expected is { Length: > 0 }) MatchNativeBindingChunk(managerMesh, info, chunkName, arrayName, expected);
            }
            private static void MatchNativeBindingChunk(object managerMesh, object info, string chunkName,
                string arrayName, Array expected)
            {
                var chunk = Get(info, chunkName); var values = Get(managerMesh, arrayName);
                var start = (int)Get(chunk, "startIndex"); var length = (int)Get(chunk, "dataLength");
                if (expected == null || expected.Rank != 1 || expected.Length != length || start < 0 ||
                    start > (int)Get(values, "Length") - length)
                    throw new InvalidOperationException("A native shared binding chunk has incompatible dimensions: " + arrayName);
                if (length == 0) return;
                var key = (expected.GetType(), values.GetType());
                Func<Array, object, int, int, bool> matches;
                lock (NativeBindingMatchers)
                {
                    if (!NativeBindingMatchers.TryGetValue(key, out matches))
                        NativeBindingMatchers.Add(key, matches = CreateNativeBindingMatcher(key.Item1, key.Item2));
                }
                if (!matches(expected, values, start, length))
                    throw new InvalidOperationException("A native shared identity has different binding content: " + arrayName);
            }
            private static Func<Array, object, int, int, bool> CreateNativeBindingMatcher(Type arrayType, Type containerType)
            {
                var expected = Expression.Parameter(typeof(Array), "expected");
                var actual = Expression.Parameter(typeof(object), "actual");
                var start = Expression.Parameter(typeof(int), "start");
                var length = Expression.Parameter(typeof(int), "length");
                var item = containerType.GetProperty("Item", Fields);
                if (item == null)
                    return Expression.Lambda<Func<Array, object, int, int, bool>>(
                        NativeBindingError(typeof(bool), new MissingMemberException(containerType.FullName, "Item")), expected, actual, start, length).Compile();
                var array = Expression.Variable(arrayType, "array");
                var container = Expression.Variable(containerType, "container");
                var index = Expression.Variable(typeof(int), "index");
                var left = Expression.Variable(arrayType.GetElementType(), "left");
                var right = Expression.Variable(item.PropertyType, "right");
                var stop = Expression.Label(typeof(bool));
                var compare = NativeBindingComparison(left, right);
                var body = Expression.Block(new[] { array, container, index, left, right },
                    Expression.Assign(array, Expression.Convert(expected, arrayType)),
                    Expression.Assign(container, Expression.Convert(actual, containerType)),
                    Expression.Assign(index, Expression.Constant(0)),
                    Expression.Loop(Expression.IfThenElse(Expression.LessThan(index, length),
                        Expression.Block(Expression.Assign(left, Expression.ArrayIndex(array, index)),
                            Expression.Assign(right, Expression.Property(container, item, Expression.Add(start, index))),
                            Expression.IfThen(Expression.Not(compare), Expression.Break(stop, Expression.Constant(false))),
                            Expression.PostIncrementAssign(index), Expression.Empty()),
                        Expression.Break(stop, Expression.Constant(true))), stop));
                return Expression.Lambda<Func<Array, object, int, int, bool>>(body, expected, actual, start, length).Compile();
            }
            private static bool SameNativeBindingValue(object expected, object actual)
            {
                if (expected == null || actual == null) return expected == null && actual == null;
                var key = (expected.GetType(), actual.GetType());
                Func<object, object, bool> compare;
                lock (NativeBindingValues)
                {
                    if (!NativeBindingValues.TryGetValue(key, out compare))
                    {
                        var left = Expression.Parameter(typeof(object), "expected");
                        var right = Expression.Parameter(typeof(object), "actual");
                        compare = Expression.Lambda<Func<object, object, bool>>(
                            NativeBindingComparison(Expression.Convert(left, key.Item1), Expression.Convert(right, key.Item2), true), left, right).Compile();
                        NativeBindingValues.Add(key, compare);
                    }
                }
                return compare(expected, actual);
            }
            private static unsafe int NativeBindingFloatBits(float value)
            {
                var bits = *(int*)&value;
                if ((bits & 0x7f800000) == 0x7f800000)
                    throw new InvalidOperationException("Native shared binding contains a nonfinite scalar.");
                return bits;
            }
            private static Expression NativeBindingComparison(Expression expected, Expression actual, bool runtimeTypes = false)
            {
                var leftType = expected.Type; var rightType = actual.Type;
                var leftNullable = Nullable.GetUnderlyingType(leftType); var rightNullable = Nullable.GetUnderlyingType(rightType);
                if (leftNullable != null || rightNullable != null)
                {
                    var leftNull = NativeBindingNull(expected); var rightNull = NativeBindingNull(actual);
                    return Expression.Condition(Expression.OrElse(leftNull, rightNull), Expression.AndAlso(leftNull, rightNull),
                        NativeBindingComparison(leftNullable == null ? expected : Expression.Property(expected, "Value"),
                            rightNullable == null ? actual : Expression.Property(actual, "Value"), runtimeTypes));
                }
                if (leftType == typeof(float) && rightType == typeof(float))
                {
                    var bits = typeof(SourceGraph).GetMethod(nameof(NativeBindingFloatBits), BindingFlags.Static | BindingFlags.NonPublic);
                    return Expression.Equal(Expression.Call(bits, expected), Expression.Call(bits, actual));
                }
                if (leftType == typeof(Vector2))
                {
                    Expression Axis(string name)
                    {
                        var field = NativeField(rightType, name);
                        var property = field == null ? rightType.GetProperty(name, Fields) : null;
                        if (field == null && property == null)
                            return NativeBindingError(typeof(bool), new MissingMemberException(rightType.FullName, name));
                        var member = field != null ? (Expression)Expression.Field(actual, field) : Expression.Property(actual, property);
                        var value = Expression.Variable(member.Type, name);
                        return Expression.Block(new[] { value }, Expression.Assign(value, member),
                            Expression.Condition(NativeBindingNull(value),
                                NativeBindingError(typeof(bool), new MissingMemberException(rightType.FullName, name)),
                                NativeBindingComparison(Expression.Field(expected, name), value)));
                    }
                    return Expression.AndAlso(Expression.Not(NativeBindingNull(actual)), Expression.AndAlso(Axis("x"), Axis("y")));
                }
                if (!runtimeTypes && (!leftType.IsValueType || !rightType.IsValueType))
                    return Expression.Call(typeof(SourceGraph).GetMethod(nameof(SameNativeBindingValue), BindingFlags.Static | BindingFlags.NonPublic),
                        Expression.Convert(expected, typeof(object)), Expression.Convert(actual, typeof(object)));
                if (leftType != rightType) return Expression.Constant(false);
                if (leftType.IsEnum) return Expression.Equal(expected, actual);
                if (leftType.IsPrimitive)
                    return Expression.Call(expected, leftType.GetMethod(nameof(Equals), new[] { leftType }), actual);
                if (!leftType.IsValueType)
                    return NativeBindingError(typeof(bool), new InvalidOperationException("Native shared binding has an unsupported reference value."));
                var fields = leftType.GetFields(Fields);
                if (fields.Length == 0)
                    return NativeBindingError(typeof(bool), new InvalidOperationException("Native shared binding has no inspectable value fields."));
                return fields.Select(field => NativeBindingComparison(Expression.Field(expected, field), Expression.Field(actual, field)))
                    .Aggregate(Expression.AndAlso);
            }
            private static Expression NativeBindingNull(Expression value) => Nullable.GetUnderlyingType(value.Type) != null
                ? Expression.Not(Expression.Property(value, "HasValue"))
                : value.Type.IsValueType ? Expression.Constant(false) : Expression.ReferenceEqual(value, Expression.Constant(null, value.Type));
            private static Expression NativeBindingMember(Expression value, string name) => NativeField(value.Type, name) is { } field
                ? Expression.Field(value, field) : Expression.Property(value, value.Type.GetProperty(name, Fields) ??
                    throw new MissingMemberException(value.Type.FullName, name));
            private static Expression NativeBindingError(Type type, Exception error) => Expression.Throw(Expression.New(
                error.GetType().GetConstructor(new[] { typeof(string) }), Expression.Constant(error.Message)), type);

            private void ValidateProspectiveData()
            {
                object FutureData(object deformer) => _data.FirstOrDefault(data => ReferenceEquals(data.Deformer, deformer))?.Replacement ?? Get(deformer, "MeshData");
                foreach (var owner in _owners.Where(owner => owner.Order == 1))
                {
                    var children = ((IEnumerable)Get(owner.Deformer, "renderDeformerList")).Cast<Component>().ToArray();
                    var virtualData = FutureData(owner.Deformer);
                    var childData = (IList)Get(virtualData, "childDataList");
                    if (children.Length != childData.Count)
                        throw new InvalidOperationException("Prepared native mesh data changed child order or count.");
                    for (var index = 0; index < children.Length; index++)
                    {
                        var render = Get(children[index], "Deformer");
                        var skin = _skins.Single(item => ReferenceEquals(item.Deformer, render));
                        if ((int)Get(childData[index], "vertexCount") != skin.Replacement.vertexCount ||
                            (int)Get(FutureData(render), "vertexCount") != skin.Replacement.vertexCount)
                            throw new InvalidOperationException("Prepared render and child bindings differ from the replacement topology.");
                    }
                }
                foreach (var owner in _owners.Where(owner => owner.Order == 0 && !owner.PreservesRejectedCloth))
                {
                    var parent = _owners.SingleOrDefault(parent => parent.Order == 1 && parent.Component == owner.Parent);
                    if (parent == null || owner.ClothData == null)
                        throw new InvalidOperationException("A native cloth consumer has no resolved virtual source.");
                    var count = (int)Get(FutureData(parent.Deformer), "vertexCount");
                    var vertices = (IList)Get(owner.ClothData, "useVertexList");
                    var selection = (IList)Get(owner.ClothData, "selectionData");
                    if (vertices.Count != selection.Count)
                        throw new InvalidOperationException("A native cloth consumer has inconsistent vertex selections.");
                    for (var index = 0; index < vertices.Count; index++)
                        if ((int)selection[index] != 0 && ((int)vertices[index] < 0 || (int)vertices[index] >= count))
                            throw new InvalidOperationException("A native cloth consumer registers outside its virtual source.");
                }
                foreach (var skin in _skins)
                {
                    if (skin.Replacement.blendShapeCount < skin.Original.blendShapeCount)
                        throw new InvalidOperationException("A skin transition removed an existing expression.");
                    for (var shape = 0; shape < skin.Original.blendShapeCount; shape++)
                    {
                        if (skin.Original.GetBlendShapeName(shape) != skin.Replacement.GetBlendShapeName(shape) ||
                            skin.Original.GetBlendShapeFrameCount(shape) != skin.Replacement.GetBlendShapeFrameCount(shape))
                            throw new InvalidOperationException("A skin transition changed an existing expression layout.");
                        for (var frame = 0; frame < skin.Original.GetBlendShapeFrameCount(shape); frame++)
                            if (skin.Original.GetBlendShapeFrameWeight(shape, frame) != skin.Replacement.GetBlendShapeFrameWeight(shape, frame))
                                throw new InvalidOperationException("A skin transition changed an existing expression frame weight.");
                    }
                }
            }

            private void ValidateRelations(bool? installed = null)
            {
                foreach (var owner in _owners.Where(owner => owner.Order == 1 && owner.Component))
                {
                    var children = ((IEnumerable)Get(owner.Deformer, "renderDeformerList")).Cast<Component>().ToArray();
                    var hashes = (IList)Get(owner.Deformer, "renderDeformerHashList");
                    var data = Get(owner.Deformer, "MeshData");
                    var childData = (IList)Get(data, "childDataList");
                    if (children.Length != hashes.Count || children.Length != childData.Count)
                        throw new InvalidOperationException("Native child order, declarations and data disagree.");
                    for (var index = 0; index < children.Length; index++)
                    {
                        var render = Get(children[index], "Deformer");
                        var source = SourceMesh.GetValue(render) as Mesh;
                        // ChildData's hash belongs to MeshData verification, not the render component identity.
                        if (!source || (int)hashes[index] != (int)Get(children[index], "SaveDataHash") ||
                            (int)Get(childData[index], "vertexCount") != source.vertexCount)
                            throw new InvalidOperationException("A native child binding differs from its declared render source.");
                    }
                }
                if (installed.HasValue)
                    foreach (var owner in _owners)
                        if (owner.Component)
                        {
                            var result = Convert.ToInt32(Invoke(owner.Component, "VerifyData"));
                            if (installed.Value && owner.PreservesRejectedCloth)
                            {
                                if (result == 0 || Flag(Get(owner.Component, "Status"), "IsInitComplete"))
                                    throw new InvalidOperationException("A rejected native cloth consumer was enabled by a source transition.");
                                continue;
                            }
                            var expected = installed.Value ? 0 : owner.VerificationResult;
                            if (result != expected)
                                throw new InvalidOperationException($"The native cloth graph did not verify for {owner.Component.GetType().FullName} ({owner.Component.name}): {result}; expected {expected}.");
                        }
            }

            private void ValidatePreparedSharedBindings()
            {
                var virtualData = _owners.Where(owner => owner.Order == 1).Select(owner =>
                        _data.SingleOrDefault(data => ReferenceEquals(data.Deformer, owner.Deformer))?.Replacement)
                    .Where(data => data).ToArray();
                var parents = new Dictionary<int, UnityEngine.Object>();
                var children = new Dictionary<long, (int Parent, object Row)>();
                foreach (var data in virtualData)
                {
                    var uid = (int)Get(data, "SaveDataHash");
                    if (parents.TryGetValue(uid, out var previous) && !ReferenceEquals(previous, data) &&
                        HashNativeData(previous) != HashNativeData(data))
                        throw new InvalidOperationException("Prepared native virtual sources have the same identity and different content.");
                    parents[uid] = data;
                    var rows = (IList)Get(data, "childDataList");
                    for (var index = 0; index < rows.Count; index++)
                    {
                        var childUid = unchecked((long)uid << ((16 + index) & 63));
                        var row = rows[index];
                        if (children.TryGetValue(childUid, out var child) &&
                            (child.Parent != uid || !ReferenceEquals(child.Row, row) &&
                             HashNativeData(new[] { Get(child.Row, "vertexInfoList"), Get(child.Row, "vertexWeightList") }) !=
                             HashNativeData(new[] { Get(row, "vertexInfoList"), Get(row, "vertexWeightList") })))
                            throw new InvalidOperationException("Prepared native child sources have conflicting shared identities.");
                        children[childUid] = (uid, row);
                    }
                }
                if (virtualData.Length == 0 || !(bool)InvokeStatic(ManagerType, "IsInstance")) return;
                var managerMesh = Get(GetStatic(ManagerType, "Instance"), "Mesh");
                foreach (var data in virtualData) ValidateNativeSharedBindings(managerMesh, data);
            }

            private void CaptureNativeInitialization()
            {
                foreach (var owner in _owners)
                {
                    if (!owner.Component || owner.Initialized || owner.PreservesRejectedCloth) continue;
                    var status = Get(owner.Component, "Status");
                    if (!Flag(status, "IsInitSuccess") || Flag(status, "IsDispose")) continue;
                    // Unity Start can complete after the source was captured. Its one-time
                    // initialization becomes part of the native lifecycle being restored.
                    owner.Initialized = true;
                    owner.UserEnabled = (bool)Get(status, "userEnable");
                    if (owner.Deformer == null) continue;
                    status = Get(owner.Deformer, "Status");
                    owner.DeformerUserEnabled = (bool)Get(status, "userEnable");
                    owner.DeformerEnabled = (bool)Get(status, "enable");
                }
            }

            private void ValidatePaletteDonors()
            {
                foreach (var pair in _paletteBindings)
                {
                    var skin = _skins.Single(value => value.Renderer == pair.Key);
                    var expected = PreparePalette(pair.Key, _changes[pair.Key], pair.Value);
                    if (expected == null ? skin.ReplacementBones != null : skin.ReplacementBones == null || !expected.SequenceEqual(skin.ReplacementBones))
                        throw new InvalidOperationException("An append-only palette donor changed before source application.");
                }
            }

            internal bool CanReapply()
            {
                if (_started || _applied || !_graphics || _skins.Any(skin => !skin.Renderer || !skin.Original || !skin.Replacement ||
                    skin.Renderer is SkinnedMeshRenderer renderer && !renderer.bones.SequenceEqual(skin.Bones)) ||
                    _owners.Any(owner => !owner.Component) || _data.Any(data => !data.Original || !data.Replacement)) return false;
                try
                {
                    ValidateRestoredState();
                    ValidateRelations(false);
                    ValidatePaletteDonors();
                    CaptureNativeInitialization();
                    foreach (var data in _data) ValidateSavedFields(data.Original, data.OriginalIdentity);
                    foreach (var owner in _owners)
                    {
                        var status = Get(owner.Component, "Status");
                        if (Flag(status, "IsInitComplete") != owner.Initialized ||
                            (bool)Get(status, "userEnable") != owner.UserEnabled) return false;
                        if (owner.Deformer == null) continue;
                        status = Get(owner.Deformer, "Status");
                        if ((bool)Get(status, "userEnable") != owner.DeformerUserEnabled ||
                            (bool)Get(status, "enable") != owner.DeformerEnabled) return false;
                    }
                    return true;
                }
                catch (InvalidOperationException) { return false; }
            }

            internal void Apply(bool restart = true)
            {
                if (_started) throw new InvalidOperationException("The native skin source is already in a transition.");
                ValidateSources(false);
                CaptureNativeInitialization();
                _installed.Clear();
                ValidatePaletteDonors();
                foreach (var skin in _skins)
                {
                    CaptureWeights(skin);
                    skin.OriginalWeightChannels = (ValueTuple<string, int>[])skin.WeightChannels.Clone();
                    skin.OriginalWeights = (float[])skin.Weights.Clone();
                }
                CaptureDonorWeights();
                if (_data.Count != 0)
                {
                    CompleteJobs();
                    ValidatePreparedSharedBindings();
                }
                try
                {
                    _started = true;
                    CompleteJobs(); DisposeOwners();
                    ClearWeights();
                    foreach (var data in _data) Put(data.Deformer, "MeshData", data.Replacement);
                    foreach (var skin in _skins)
                    {
                        if (skin.Deformer != null) SourceMesh.SetValue(skin.Deformer, skin.Replacement);
                        SetMesh(skin.Renderer, skin.Replacement);
                        if (skin.Renderer is SkinnedMeshRenderer renderer && skin.ReplacementBones != null)
                            renderer.bones = skin.ReplacementBones;
                    }
                    RestoreWeights(true);
                    if (_data.Count != 0) CreateVerification();
                    ValidateRelations();
                    if (restart) InitOwners();
                    PrepareNativeSurfaceFinishes();
                    RestoreWeights(true);
                    ValidateRelations(true);
                    var current = Capture(_graphics, _changes.Keys);
                    if (current == null || _changes.Any(pair => !current.TryGetValue(pair.Key, out var value) ||
                        value.Original != pair.Value.Mesh || !value.OwnerCurrent || !value.AcceptsCurrentMesh()))
                        throw new InvalidOperationException("The changed skin source was not retained by its native owner.");
                    foreach (var pair in current) _installed.Add(pair.Key, pair.Value);
                    _applied = true;
                }
                catch (Exception failure)
                {
                    try { ValidateSources(true); RestoreCore(true); _started = false; }
                    catch (Exception rollback) { throw new AggregateException("Native skin transition and rollback failed.", failure, rollback); }
                    throw;
                }
            }

            internal void Restore(bool restart)
            {
                if (!_started) return;
                ValidateSources(true);
                CaptureNativeInitialization();
                if (_applied)
                    foreach (var skin in _skins) CaptureWeights(skin);
                _applied = false;
                RestoreCore(restart); _applied = _started = false;
            }
            private void RestoreCore(bool restart)
            {
                CompleteJobs();
                foreach (var finish in _nativeSurfaceFinishes.Values) finish.Dispose();
                _nativeSurfaceFinishes.Clear();
                DisposeOwners();
                ClearWeights();
                foreach (var data in _data) Put(data.Deformer, "MeshData", data.Original);
                foreach (var skin in _skins)
                {
                    if (skin.Deformer != null) SourceMesh.SetValue(skin.Deformer, skin.Original);
                    if (skin.Renderer && !skin.Superseded)
                    {
                        if (skin.Renderer is SkinnedMeshRenderer renderer && skin.ReplacementBones != null)
                            renderer.bones = skin.Bones;
                        SetMesh(skin.Renderer, skin.Original);
                    }
                }
                RestoreWeights(false);
                RestoreVerification();
                if (restart) InitOwners(true);
                RestoreWeights(false);
                ValidateRestoredState();
                ValidateRelations(false);
            }
            private void ValidateRestoredState()
            {
                ValidateSources(false);
                foreach (var owner in _owners)
                {
                    if (!owner.Component) continue;
                    if (owner.PreservesRejectedCloth && Flag(Get(owner.Component, "Status"), "IsInitComplete"))
                        throw new InvalidOperationException("A rejected native cloth consumer unexpectedly initialized while its source was owned.");
                    if ((int)Get(owner.Component, "dataHash") != owner.Hash ||
                        (int)Get(owner.Component, "dataVersion") != owner.Version ||
                        !ReferenceEquals(Optional(owner.Component, "ClothData"), owner.ClothData) ||
                        !ReferenceEquals(Optional(owner.Component, "ClothSelection"), owner.ClothSelection))
                        throw new InvalidOperationException("The native cloth data owner or verification identity was not restored.");
                    ValidateSavedFields(owner.Component, owner.Verification);
                    if (owner.ClothData != null) ValidateSavedFields(owner.ClothData, owner.ClothDataIdentity);
                    if (owner.ClothSelection != null) ValidateSavedFields(owner.ClothSelection, owner.ClothSelectionIdentity);
                    if (owner.Deformer == null) continue;
                    if ((int)Get(owner.Deformer, "dataHash") != owner.DeformerHash.Value ||
                        (int)Get(owner.Deformer, "dataVersion") != owner.DeformerVersion.Value)
                        throw new InvalidOperationException("The native deformer verification identity was not restored.");
                    ValidateSavedFields(owner.Deformer, owner.DeformerVerification);
                }
            }
            private void DisposeOwners()
            {
                foreach (var owner in _owners.OrderBy(owner => owner.Order))
                    if (owner.Component && !owner.PreservesRejectedCloth && (owner.Initialized || Flag(Get(owner.Component, "Status"), "IsInitStart")))
                    {
                        Invoke(owner.Component, "SetUserEnable", false);
                        if (owner.Deformer != null) Invoke(owner.Deformer, "OnDisable");
                    }
                foreach (var owner in _owners.OrderBy(owner => owner.Order))
                    if (owner.Component && !owner.PreservesRejectedCloth && (owner.Initialized || Flag(Get(owner.Component, "Status"), "IsInitStart")))
                        Invoke(owner.Component, "TacticalDispose");
                foreach (var owner in _owners.Where(owner => owner.Order == 1))
                    ((IList)Get(owner.Deformer, "sharedChildMeshIndexList")).Clear();
            }
            private void RestoreVerification()
            {
                foreach (var owner in _owners)
                {
                    if (!owner.Component) continue;
                    Put(owner.Component, "dataHash", owner.Hash); Put(owner.Component, "dataVersion", owner.Version);
                    RestoreFields(owner.Component, owner.Verification);
                    if (owner.Deformer == null) continue;
                    Put(owner.Deformer, "dataHash", owner.DeformerHash.Value); Put(owner.Deformer, "dataVersion", owner.DeformerVersion.Value);
                    RestoreFields(owner.Deformer, owner.DeformerVerification);
                }
            }
            private void InitOwners(bool restoring = false)
            {
                var verifyInitialization = restoring && _owners.Any(owner => owner.Component && owner.Initialized && owner.VerificationResult != 0);
                if (verifyInitialization) ValidateRelations(false);
                try
                {
                    // Native Start can initialize an owner after a presentation was prepared.
                    // Its restored serialized references can still carry a pre-existing mismatch.
                    // Reinitialize against the current source, then retain the original identity.
                    if (verifyInitialization) CreateVerification();
                    foreach (var owner in _owners.OrderByDescending(owner => owner.Order))
                    {
                        if (!owner.Component || !owner.Initialized) continue;
                        var status = Get(owner.Component, "Status");
                        if (Flag(status, "IsDispose")) Invoke(status, "SetDispose", false);
                        Invoke(owner.Component, "SetUserEnable", false);
                        Invoke(owner.Component, "Init");
                        if (!Flag(Get(owner.Component, "Status"), "IsInitSuccess"))
                            throw new InvalidOperationException($"The native cloth owner did not reinitialize for {owner.Component.GetType().FullName} " +
                                $"({owner.Component.GetInstanceID()}): verify={Invoke(owner.Component, "VerifyData")}; originalVerify={owner.VerificationResult}; " +
                                $"init={Get(status, "init")}; initStart={Get(status, "initStart")}; initError={Get(status, "initError")}; " +
                                $"disposed={Get(status, "dispose")}; active={owner.Component.gameObject.activeInHierarchy}.");
                    }
                }
                finally
                {
                    if (verifyInitialization) RestoreVerification();
                }
                foreach (var owner in _owners.Where(owner => owner.Component && owner.Initialized && owner.Deformer != null))
                {
                    var status = Get(owner.Deformer, "Status");
                    Invoke(status, "SetUserEnable", false);
                    Invoke(status, "UpdateStatus");
                }
                foreach (var owner in _owners.OrderBy(owner => owner.Order))
                    if (owner.Component && owner.Initialized) Invoke(owner.Component, "SetUserEnable", owner.UserEnabled);
                foreach (var owner in _owners.OrderBy(owner => owner.Order))
                    if (owner.Component && owner.Initialized && owner.Deformer != null)
                    {
                        var status = Get(owner.Deformer, "Status");
                        Invoke(status, "SetEnable", owner.DeformerEnabled);
                        Invoke(status, "SetUserEnable", owner.DeformerUserEnabled);
                        Invoke(status, "UpdateStatus");
                    }
            }
            private void CreateVerification()
            {
                foreach (var owner in _owners.OrderByDescending(owner => owner.Order))
                {
                    // Source replacement must not turn a natively rejected, uninitialized
                    // cloth consumer into a new simulation. Its render sources remain usable.
                    if (owner.PreservesRejectedCloth) continue;
                    if (owner.Deformer != null) Invoke(owner.Deformer, "CreateVerifyData");
                    Invoke(owner.Component, "CreateVerifyData");
                }
            }
            private static (string Name, int Occurrence)[] PhysicalWeightChannels(Mesh mesh)
            {
                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                var result = new (string Name, int Occurrence)[mesh.blendShapeCount];
                for (var index = 0; index < result.Length; index++)
                {
                    var name = mesh.GetBlendShapeName(index) ?? string.Empty;
                    seen.TryGetValue(name, out var occurrence);
                    result[index] = (name, occurrence);
                    seen[name] = occurrence + 1;
                }
                return result;
            }
            private static Dictionary<(string Name, int Occurrence), float> PhysicalWeights(
                (string Name, int Occurrence)[] channels, float[] values)
            {
                if (channels == null || values == null || channels.Length != values.Length)
                    throw new InvalidOperationException("A physical channel weight snapshot is incomplete.");
                return channels.Select((channel, index) => (channel, values[index]))
                    .ToDictionary(value => value.channel, value => value.Item2);
            }
            private static void CaptureWeights(NativeSkin skin)
            {
                if (skin.Renderer is not SkinnedMeshRenderer renderer || !renderer || !renderer.sharedMesh) return;
                skin.WeightChannels = PhysicalWeightChannels(renderer.sharedMesh);
                skin.Weights = Enumerable.Range(0, skin.WeightChannels.Length).Select(renderer.GetBlendShapeWeight).ToArray();
            }
            private void CaptureDonorWeights()
            {
                _donorWeights.Clear();
                foreach (var binding in _paletteBindings.Values)
                {
                    if (binding.PaletteSource is not { } donor || _donorWeights.ContainsKey(donor.Renderer)) continue;
                    var renderer = donor.Renderer;
                    if (!renderer || !renderer.sharedMesh)
                        throw new InvalidOperationException("A declared physical channel donor has no current mesh.");
                    var channels = PhysicalWeightChannels(renderer.sharedMesh);
                    _donorWeights.Add(renderer, PhysicalWeights(channels,
                        Enumerable.Range(0, channels.Length).Select(renderer.GetBlendShapeWeight).ToArray()));
                }
            }
            private void ClearWeights()
            {
                foreach (var skin in _skins)
                {
                    if (skin.Superseded || skin.Renderer is not SkinnedMeshRenderer renderer || !renderer || !renderer.sharedMesh) continue;
                    for (var index = 0; index < renderer.sharedMesh.blendShapeCount; index++) renderer.SetBlendShapeWeight(index, 0f);
                }
            }
            private void RestoreWeights(bool installed)
            {
                foreach (var skin in _skins)
                {
                    if (skin.Superseded || skin.Renderer is not SkinnedMeshRenderer renderer || !renderer) continue;
                    var mesh = installed ? skin.Replacement : skin.Original;
                    if (!mesh) continue;
                    var own = PhysicalWeights(skin.WeightChannels, skin.Weights);
                    var original = !installed && skin.OriginalWeightChannels != null
                        ? PhysicalWeights(skin.OriginalWeightChannels, skin.OriginalWeights) : null;
                    Dictionary<(string Name, int Occurrence), float> foreign = null;
                    if (installed && _paletteBindings.TryGetValue(renderer, out var binding) && binding.PaletteSource is { } donor &&
                        !_donorWeights.TryGetValue(donor.Renderer, out foreign))
                        throw new InvalidOperationException("A physical channel donor was not captured before source replacement.");
                    var channels = PhysicalWeightChannels(mesh);
                    for (var index = 0; index < channels.Length; index++)
                    {
                        var channel = channels[index];
                        if (!own.TryGetValue(channel, out var value) &&
                            !(original != null && original.TryGetValue(channel, out value)) &&
                            !(foreign != null && foreign.TryGetValue(channel, out value))) value = 0f;
                        renderer.SetBlendShapeWeight(index, value);
                    }
                }
            }
            private void CompleteJobs()
            {
                if (_owners.Count == 0 || !(bool)InvokeStatic(ManagerType, "IsInstance")) return;
                var manager = GetStatic(ManagerType, "Instance");
                Invoke(Get(manager, "Compute"), "CompleteJob");
            }

            private void ValidateSources(bool installed)
            {
                ValidateBoundaryOwners();
                foreach (var owner in _owners)
                {
                    if (!owner.Component) continue;
                    if (!ReferenceEquals(Optional(owner.Component, "Deformer"), owner.BoundDeformer) ||
                        owner.Order == 0 && !ReferenceEquals(Optional(owner.Component, "virtualDeformer"), owner.Parent) ||
                        owner.Order == 2 && ResolveNativeRenderer(owner.Component, owner.Deformer) != owner.Target)
                        throw new InvalidOperationException("A different native owner generation prevents skin restoration.");
                    if (owner.Order != 1) continue;
                    var children = ((IEnumerable)Get(owner.Deformer, "renderDeformerList")).Cast<Component>().ToArray();
                    if (children.Length != owner.Children.Length ||
                        children.Where((child, index) => !ReferenceEquals(child, owner.Children[index])).Any())
                        throw new InvalidOperationException("A different native child order prevents skin restoration.");
                }
                foreach (var skin in _skins)
                {
                    if (!skin.Renderer) continue;
                    var expected = installed ? skin.Replacement : skin.Original;
                    var source = skin.Deformer == null ? CurrentMesh(skin.Renderer) : SourceMesh.GetValue(skin.Deformer) as Mesh;
                    if (_started && skin.Deformer == null && source != skin.Replacement &&
                        (installed || source != skin.Original))
                    {
                        // A newer ordinary-skin owner keeps its mesh and weights.
                        skin.Superseded = true;
                        continue;
                    }
                    if (source != expected && (_applied || source != skin.Original))
                        throw new InvalidOperationException("A different source owns a renderer in this skin transition.");
                    if (skin.Renderer is SkinnedMeshRenderer skinRenderer && _paletteBindings.ContainsKey(skinRenderer) &&
                        !ReferenceEquals(skinRenderer.rootBone, skin.RootBone))
                        throw new InvalidOperationException("A different source owns this renderer's root bone.");
                    if (skin.ReplacementBones != null && skin.Renderer is SkinnedMeshRenderer paletteRenderer &&
                        !(installed ? skin.ReplacementBones : skin.Bones).SequenceEqual(paletteRenderer.bones) &&
                        (_applied || !skin.Bones.SequenceEqual(paletteRenderer.bones) && !skin.ReplacementBones.SequenceEqual(paletteRenderer.bones)))
                        throw new InvalidOperationException("A different source owns this renderer's bone palette.");
                    if (skin.Deformer == null) continue;
                    var draw = CurrentMesh(skin.Renderer);
                    if (draw != source && draw != RuntimeMesh.GetValue(skin.Deformer) as Mesh)
                        throw new InvalidOperationException("A different owner replaced the native render binding.");
                }
                foreach (var data in _data)
                {
                    var expected = installed ? data.Replacement : data.Original;
                    var actual = Get(data.Deformer, "MeshData");
                    if (!ReferenceEquals(actual, expected) && (_applied || !ReferenceEquals(actual, data.Original)))
                        throw new InvalidOperationException("A different native data owner prevents skin restoration.");
                }
            }
            private void ValidateBoundaryOwners()
            {
                if (_boundaryVirtualOwners.Count == 0) return;
                var virtuals = _graphics.GetComponentsInChildren(VirtualType, true).Cast<Component>().Where(component =>
                    ((IEnumerable)Get(Get(component, "Deformer"), "renderDeformerList")).Cast<Component>().Any(_boundaryRenderOwners.Contains)).ToArray();
                var cloths = _graphics.GetComponentsInChildren(ClothBaseType, true).Cast<Component>().Where(component =>
                    Optional(component, "virtualDeformer") is Component parent && _boundaryVirtualOwners.Contains(parent)).ToArray();
                if (virtuals.Length != _boundaryVirtualOwners.Count || virtuals.Any(component => !_boundaryVirtualOwners.Contains(component)) ||
                    cloths.Length != _boundaryClothVertices.Count || cloths.Any(component => !_boundaryClothVertices.Keys.Any(owner => owner.Component == component)))
                    throw new InvalidOperationException("A different native consumer generation invalidated the boundary skin proof.");
                foreach (var pair in _boundaryClothVertices)
                    if (!pair.Key.Component || !ReferenceEquals(Optional(pair.Key.Component, "ClothData"), pair.Key.ClothData) ||
                        !((IEnumerable)Get(pair.Key.ClothData, "useVertexList")).Cast<int>().SequenceEqual(pair.Value))
                        throw new InvalidOperationException("A native cloth registration changed after the boundary skin proof was prepared.");
            }
            private static Mesh CurrentMesh(Renderer renderer) => renderer is SkinnedMeshRenderer skin
                ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
            private static void SetMesh(Renderer renderer, Mesh mesh)
            {
                if (renderer is SkinnedMeshRenderer skin) skin.sharedMesh = mesh;
                else if (renderer.GetComponent<MeshFilter>() is { } filter) filter.sharedMesh = mesh;
                else throw new InvalidOperationException("The native render target has no mesh binding.");
            }
            private static bool Flag(object value, string name) => (bool)Get(value, name);
            private static Dictionary<string, object> SaveFields(object value, params string[] names)
            {
                var result = new Dictionary<string, object>();
                foreach (var name in names)
                {
                    var field = NativeField(value.GetType(), name);
                    if (field == null) continue;
                    result.Add(name, field.GetValue(value) is IList list ? list.Cast<object>().ToArray() : field.GetValue(value));
                }
                return result;
            }
            private static void RestoreFields(object value, Dictionary<string, object> saved)
            {
                foreach (var pair in saved)
                {
                    var field = NativeField(value.GetType(), pair.Key);
                    if (field.GetValue(value) is IList list && pair.Value is object[] entries)
                    { list.Clear(); foreach (var entry in entries) list.Add(entry); }
                    else field.SetValue(value, pair.Value);
                }
            }
            private static void ValidateSavedFields(object value, Dictionary<string, object> saved)
            {
                foreach (var pair in saved)
                {
                    var actual = NativeField(value.GetType(), pair.Key).GetValue(value);
                    var equal = actual is IList list && pair.Value is object[] entries
                        ? list.Cast<object>().SequenceEqual(entries) : Equals(actual, pair.Value);
                    if (!equal)
                        throw new InvalidOperationException($"Native verification field {value.GetType().FullName}.{pair.Key} was not restored.");
                }
            }
            private static readonly Dictionary<(Type Type, string Name), FieldInfo> NativeFields = new();
            private static readonly Dictionary<(Type Type, string Name), Func<object, object>> NativeReaders = new();
            private static readonly Dictionary<Type, FinishNativeArrayLayout> NativeSurfaceArrayLayouts = new();

            private static FinishNativeArrayLayout NativeSurfaceArrayLayout(Type arrayType, string elementName, int stride)
            {
                if (elementName != "Unity.Mathematics.float3" && elementName != "Unity.Mathematics.float4" &&
                    elementName != "UnityEngine.BoneWeight1")
                    throw new InvalidOperationException("A final surface native element type is unsupported.");
                lock (NativeSurfaceArrayLayouts)
                {
                    if (NativeSurfaceArrayLayouts.TryGetValue(arrayType, out var layout))
                    {
                        if (layout.ElementName != elementName || layout.Stride != stride)
                            throw new InvalidOperationException("A final surface native element changed its declared layout.");
                        return layout;
                    }
                    // The consumer owns exactly float3, float4 and BoneWeight1 closed arrays.
                    if (NativeSurfaceArrayLayouts.Count >= 3)
                        throw new InvalidOperationException("A final surface native array introduced another closed type.");
                    layout = new FinishNativeArrayLayout(arrayType, elementName, stride);
                    NativeSurfaceArrayLayouts.Add(arrayType, layout);
                    return layout;
                }
            }

            private static bool FinishIsMeshUse(object value)
            {
                var key = (value.GetType(), "IsMeshUse()");
                Func<object, object> read;
                lock (NativeReaders)
                {
                    if (!NativeReaders.TryGetValue(key, out read))
                    {
                        MethodInfo method = null;
                        for (var type = value.GetType(); type != null; type = type.BaseType)
                        {
                            method = type.GetMethods(Fields | BindingFlags.DeclaredOnly).FirstOrDefault(candidate =>
                                candidate.Name == "IsMeshUse" && candidate.GetParameters().Length == 0);
                            if (method != null) break;
                        }
                        if (method == null) throw new MissingMethodException(value.GetType().FullName, "IsMeshUse");
                        // Keep MethodInfo.Invoke's virtual dispatch and native exception wrapper.
                        // Only immutable method metadata is captured, never this live owner.
                        read = source => method.Invoke(source, null);
                        NativeReaders.Add(key, read);
                    }
                }
                return (bool)read(value);
            }


            private static FieldInfo NativeField(Type type, string name)
            {
                var key = (type, name);
                lock (NativeFields)
                {
                    if (NativeFields.TryGetValue(key, out var cached)) return cached;
                    for (var current = type; current != null; current = current.BaseType)
                        if (current.GetField(name, Fields | BindingFlags.DeclaredOnly) is { } field)
                        {
                            NativeFields.Add(key, field);
                            return field;
                        }
                    NativeFields.Add(key, null);
                    return null;
                }
            }
            private static object Optional(object value, string name)
            {
                if (value == null) return null;
                return NativeReader(value.GetType(), name)(value);
            }
            private static Func<object, object> NativeReader(Type type, string name)
            {
                var key = (type, name);
                Func<object, object> read;
                lock (NativeReaders)
                {
                    if (!NativeReaders.TryGetValue(key, out read))
                    {
                        // Retain only type metadata. Live cloth sources and boxed weight values
                        // must be read on every call so ownership checks see native changes.
                        if (NativeField(type, name) is { } field)
                        {
                            var argument = Expression.Parameter(typeof(object), "source");
                            var access = Expression.Field(Expression.Convert(argument, type), field);
                            read = Expression.Lambda<Func<object, object>>(
                                Expression.Convert(access, typeof(object)), argument).Compile();
                        }
                        else
                        {
                            // Reflection preserves boxed struct getter mutations and its exception
                            // contract; compiling an unbox conversion would read a temporary copy.
                            var property = type.GetProperty(name, Fields);
                            read = property == null ? _ => null : value => property.GetValue(value, null);
                        }
                        NativeReaders.Add(key, read);
                    }
                }
                return read;
            }
            private static object Get(object value, string name) => Optional(value, name) ??
                throw new MissingMemberException(value?.GetType().FullName, name);
            private static void Put(object value, string name, object entry)
            {
                if (NativeField(value.GetType(), name) is { } field) field.SetValue(value, entry);
                else (value.GetType().GetProperty(name, Fields) ?? throw new MissingMemberException(value.GetType().FullName, name)).SetValue(value, entry, null);
            }
            private static object Invoke(object value, string name, params object[] args)
            {
                for (var type = value.GetType(); type != null; type = type.BaseType)
                    if (type.GetMethods(Fields | BindingFlags.DeclaredOnly).FirstOrDefault(method => method.Name == name &&
                        method.GetParameters().Length == args.Length) is { } method) return method.Invoke(value, args);
                throw new MissingMethodException(value.GetType().FullName, name);
            }
            private static object InvokeStatic(Type type, string name)
            {
                for (; type != null; type = type.BaseType)
                    if (type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly) is { } method)
                        return method.Invoke(null, null);
                throw new MissingMethodException(name);
            }
            private static object GetStatic(Type type, string name)
            {
                for (; type != null; type = type.BaseType)
                    if (type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly) is { } property)
                        return property.GetValue(null, null);
                throw new MissingMemberException(name);
            }
        }

        internal static Dictionary<SkinnedMeshRenderer, SkinMeshSource> Capture(GraphicsCharacter graphics,
            IEnumerable<SkinnedMeshRenderer> renderers)
            => CapturePrefab(graphics.gameObject, renderers);

        internal static HashSet<UnityEngine.Object> NativePrefabData(GameObject prefab) => SourceGraph.NativePrefabData(prefab);
        internal static void PrepareNativeMetadata() => SourceGraph.PrepareNativeMetadata();
        internal static IEnumerator PrepareNativeFingerprint(UnityEngine.Object source, Func<bool> current, Action<string> receive) =>
            SourceGraph.PrepareNativeFingerprint(source, current, receive);
        internal static IEnumerator PrepareNativeBindings(IEnumerable<GameObject> roots,
            IReadOnlyDictionary<SkinnedMeshRenderer, (SkinMeshSource Source, Mesh Mesh)> changes,
            IReadOnlyDictionary<SkinnedMeshRenderer, SurfaceBinding> bindings,
            Func<string, bool> retainExisting, Action<string, UnityEngine.Object[]> retainPrepared,
            Func<UnityEngine.Object, IEnumerator> prepareFingerprint,
            Func<UnityEngine.Object, Func<string>, string> sourceFingerprint, Func<bool> current) =>
            SourceGraph.PrepareNativeBindings(roots, changes, bindings, retainExisting, retainPrepared, prepareFingerprint, sourceFingerprint, current);

        internal static long NativeDataMemorySize(UnityEngine.Object data) => SourceGraph.NativeDataMemorySize(data);

        internal static Dictionary<SkinnedMeshRenderer, SkinMeshSource> CapturePrefab(GameObject root,
            IEnumerable<SkinnedMeshRenderer> renderers)
        {
            var result = renderers.ToDictionary(renderer => renderer,
                renderer => new SkinMeshSource(renderer, renderer.sharedMesh));
            if (ClothType == null)
            {
                return result;
            }
            if (TargetRenderer == null || Deformer == null || SourceMesh == null || RuntimeMesh == null ||
                SkinRenderer == null || SourceTargetObject == null)
            {
                return null;
            }
            var found = new HashSet<SkinnedMeshRenderer>();
            foreach (var component in root.GetComponentsInChildren(ClothType, true))
            {
                if (!component) continue;
                var source = Deformer.GetValue(component);
                var nativeRenderer = ResolveNativeRenderer(component, source);
                if (nativeRenderer && nativeRenderer is not SkinnedMeshRenderer) continue;
                var target = nativeRenderer as SkinnedMeshRenderer;
                if (!target)
                {
                    // An unresolved native cloth relation is not evidence of an ordinary skin.
                    return null;
                }
                if (!result.ContainsKey(target)) continue;
                if (!found.Add(target) || source == null ||
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

    internal static CustomizedSubFeatureDefinitions.SubFeaturesOfType<T> GetWieldedItemFeatures<T>(
        ItemDefinition definition) where T : class
    {
        if (!definition)
        {
            return default;
        }
        var features = definition.EnumerateSubFeaturesOfType<T>();
        if (!features.HasAny && definition.IsWeapon)
        {
            features = definition.WeaponDescription.WeaponTypeDefinition.EnumerateSubFeaturesOfType<T>();
        }
        if (features.HasAny)
        {
            return features;
        }
        return new CustomizedSubFeatureDefinitions.SubFeaturesOfType<T>(GetNativeSourceGrip<T>(definition), null);
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
        private bool _gripReady;
        private float _modelNormalSign;
        private float _nativeGripAxisSign;
        private SkinnedMeshRenderer[] _armSkins;
        private SkinShape[] _skinShapes;
        private bool _refreshArmBindings;
        private int _armShapeSignature;
        private sealed class SkinShape
        {
            internal Mesh Mesh;
            internal SkinMeshSource Source;
            internal int Muscle = -1, Positive = -1, Negative = -1;
            internal float MuscleWeight, PositiveWeight, NegativeWeight;
            internal Vector3 Scale;
            internal bool Active;
        }
        private bool _plateFitReady;
        private Vector3[] _plateFitPoints;
        private Quaternion _plateFitRotation;
        private Vector3 _plateFitScale;
        private readonly struct SkinPoint(Transform bone, Vector3 local)
        {
            internal readonly Transform Bone = bone;
            internal readonly Vector3 Local = local;
        }
        private SkinPoint[] _armEnvelope;
        private Vector3[] _armInParent;
        private float _plateRoll;
        internal float PlateClearanceGap { get; private set; }
        internal WieldedGrip Grip => _gripDefinition;
        internal Mesh NativeMesh => _nativeOriginalMesh;
        internal string GripStatus { get; private set; } = "Unbound";
        private Transform[] _fingerBones;
        private AnimationClip _fingerReference;
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
        private Transform[][] _weaponLocalPaths;
        private Matrix4x4[] _weaponRelativeMatrices;
        private WieldedMechanism.Point[] _loadedLimbNormals, _currentLimbNormals;
        private LimbTransportFrame[] _limbTransportFrames;
        private Vector3[] _limbPositionValues;
        private WieldedMechanism.Point[] _limbNormalCoordinates, _limbTangentCoordinates;
        private WieldedMechanism.Point[] _stringNormalCoordinates, _stringTangentCoordinates;
        private (WieldedMechanism.Point Tangent, WieldedMechanism.Point Up, WieldedMechanism.Point Side)[]
            _loadedStringFrames, _currentStringFrames;
        private bool _mechanismGeometryReady;
        private double _mechanismQ, _mechanismSpan;
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
            _refreshArmBindings = true;
            RestoreFingers();
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
                // Face controllers can share the same hand clip. Keep its sampled
                // rotations while this owner still has the same rig and live bones.
                // A pending reference load does not invalidate that immutable sample.
                if (source && (_fingerBones == null || _fingerBones.Any(bone => !bone) ||
                               ClosedHandReference(source) != _fingerReference))
                {
                    _fingerBones = null;
                    _poseResolutionAttempted = false;
                }
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

        private static bool SameScale(Vector3 first, Vector3 second) =>
            (first - second).sqrMagnitude <= 0.00000001f * Mathf.Max(first.sqrMagnitude, second.sqrMagnitude);

        private void InvalidateSkinMeasurements()
        {
            unchecked { _armShapeSignature++; }
            _armEnvelope = null;
            _armInParent = null;
            _plateFitReady = false;
        }

        private void RefreshSkinCache()
        {
            if (_armSkins == null || _refreshArmBindings)
            {
                _refreshArmBindings = false;
                var left = _slot == EquipmentDefinitions.SlotTypeOffHand;
                var branch = _animator.GetBoneTransform(left ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder);
                if (!branch) branch = _animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
                // Only this arm branch contributes to the clearance envelope.
                // Hair and facial changes do not invalidate unchanged arm meshes.
                var skins = branch
                    ? _graphics.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                        .Where(skin => skin.sharedMesh && skin.bones.Any(bone => bone &&
                            (bone == branch || bone.IsChildOf(branch)))).ToArray()
                    : [];
                if (_armSkins == null || !_armSkins.SequenceEqual(skins))
                {
                    _armSkins = skins;
                    _skinShapes = new SkinShape[skins.Length];
                    for (var i = 0; i < _skinShapes.Length; i++) _skinShapes[i] = new SkinShape();
                    InvalidateSkinMeasurements();
                }
            }
            var changed = false;
            for (var i = 0; i < _armSkins.Length; i++)
            {
                var skin = _armSkins[i];
                var shape = _skinShapes[i];
                var active = skin && skin.enabled && skin.gameObject.activeInHierarchy && skin.sharedMesh;
                if (active != shape.Active) { shape.Active = active; changed = true; }
                if (!active) continue;
                if (shape.Source == null || !shape.Source.AcceptsCurrentMesh())
                {
                    var sources = SkinMeshSource.Capture(_graphics, new[] { skin });
                    shape.Source = sources != null && sources.TryGetValue(skin, out var source) ? source : null;
                }
                // Cloth swaps the renderer between its immutable source and its
                // simulated runtime copy. The source owner defines body shape;
                // simulation poses do not require another full skin bake.
                var mesh = shape.Source?.Original ?? skin.sharedMesh;
                if (shape.Mesh != mesh)
                {
                    shape.Mesh = mesh;
                    shape.Muscle = mesh.GetBlendShapeIndex("Muscular");
                    shape.Positive = mesh.GetBlendShapeIndex("UB_BodyVolumePositive");
                    shape.Negative = mesh.GetBlendShapeIndex("UB_BodyVolumeNegative");
                    changed = true;
                }
                var muscle = shape.Muscle >= 0 ? skin.GetBlendShapeWeight(shape.Muscle) : 0f;
                var positive = shape.Positive >= 0 ? skin.GetBlendShapeWeight(shape.Positive) : 0f;
                var negative = shape.Negative >= 0 ? skin.GetBlendShapeWeight(shape.Negative) : 0f;
                var scale = skin.transform.lossyScale;
                // Rotation and breathing perturb lossyScale's last bits. They do
                // not change the body surface and must not rebake every mesh.
                if (Mathf.Abs(muscle - shape.MuscleWeight) > 0.0001f ||
                    Mathf.Abs(positive - shape.PositiveWeight) > 0.0001f ||
                    Mathf.Abs(negative - shape.NegativeWeight) > 0.0001f || !SameScale(scale, shape.Scale))
                {
                    shape.MuscleWeight = muscle;
                    shape.PositiveWeight = positive;
                    shape.NegativeWeight = negative;
                    shape.Scale = scale;
                    changed = true;
                }
            }
            if (changed) InvalidateSkinMeasurements();
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
            var changed = !_plateFitReady || !SameScale(scale, _plateFitScale) ||
                          Quaternion.Angle(original, _plateFitRotation) > 0.5f;
            for (var i = 0; i < _armEnvelope.Length; i++)
            {
                _armInParent[i] = parent.InverseTransformVector(
                    _armEnvelope[i].Bone.TransformPoint(_armEnvelope[i].Local) - gripWorld);
                if (!changed && (_armInParent[i] - _plateFitPoints[i]).sqrMagnitude > 0.000004f) changed = true;
            }
            if (changed)
            {
                SolvePlateFit(parent, scale, axis, original);
                if (_plateFitPoints == null || _plateFitPoints.Length != _armInParent.Length)
                    _plateFitPoints = new Vector3[_armInParent.Length];
                Array.Copy(_armInParent, _plateFitPoints, _armInParent.Length);
                _plateFitRotation = original;
                _plateFitScale = scale;
                _plateFitReady = true;
            }
            return Quaternion.AngleAxis(_plateRoll, axis) * original;
        }

        private void SolvePlateFit(Transform parent, Vector3 scale, Vector3 axis, Quaternion original)
        {
            var surface = _gripDefinition.PlateSurface;
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
            var initial = _plateRoll;
            var best = Gap(initial);
            if (best < 0.006f)
            {
                var selected = initial;
                var safe = false;
                for (var delta = 5; delta <= 120 && !safe; delta += 5)
                {
                    for (var sign = -1; sign <= 1; sign += 2)
                    {
                        var angle = initial + delta * sign;
                        if (angle < -60f || angle > 60f) continue;
                        var gap = Gap(angle);
                        if (gap > best) { best = gap; selected = angle; }
                        if (gap >= 0.01f) { selected = angle; safe = true; break; }
                    }
                }
                // Refine only the selected interval, including an unreachable
                // clearance maximum. A held shield never scans 240 angles/frame.
                var coarse = selected;
                for (var angle = Mathf.Max(-60f, coarse - 5f); angle <= Mathf.Min(60f, coarse + 5f); angle += 1f)
                {
                    var gap = Gap(angle);
                    if (safe)
                    {
                        if (gap >= 0.01f && Mathf.Abs(angle - initial) < Mathf.Abs(selected - initial))
                        { selected = angle; best = gap; }
                    }
                    else if (gap > best) { best = gap; selected = angle; safe = gap >= 0.01f; }
                }
                _plateRoll = selected;
            }
            PlateClearanceGap = best;
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
                    if (!bones.Any(bone => bone && (bone == branch || bone.IsChildOf(branch)))) continue;
                    var weights = skin.sharedMesh.boneWeights;
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

        private bool MechanismCurrent()
        {
            if (!_frameFilter || !_stringFilter || _frameFilter.sharedMesh != _frameMesh ||
                _stringFilter.sharedMesh != _stringMesh || !_weaponAnimator ||
                _weaponAnimator.runtimeAnimatorController != _weaponController || !_weaponBolt ||
                !_weaponAnimator.transform.IsChildOf(transform)) return false;
            for (var i = 0; i < _weaponBones.Length; i++)
            {
                if (!_weaponBones[i] || _weaponBones[i].parent != _weaponParents[i]) return false;
                var path = _weaponLocalPaths[i];
                for (var j = 0; j < path.Length; j++)
                    if (!path[j] || path[j].parent != (j == 0 ? _weaponBones[0] : path[j - 1])) return false;
            }
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
                _weaponLocalPaths = new Transform[_weaponBones.Length][];
                _weaponRelativeMatrices = new Matrix4x4[_weaponBones.Length];
                for (var i = 0; i < _weaponBones.Length; i++)
                {
                    var path = new List<Transform>();
                    var node = _weaponBones[i];
                    while (node && node != _weaponBones[0]) { path.Add(node); node = node.parent; }
                    if (!node) return MechanismFault("NativeBoneFrameMismatch");
                    path.Reverse();
                    _weaponLocalPaths[i] = path.ToArray();
                }
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
            double x = 0, y = 0, z = 0;
            for (var i = 0; i < _weaponBones.Length; i++)
            {
                var m = _weaponRelativeMatrices[i];
                var c = coefficients[i];
                x += m.m00 * c.X + m.m01 * c.Y + m.m02 * c.Z + m.m03 * c.W;
                y += m.m10 * c.X + m.m11 * c.Y + m.m12 * c.Z + m.m13 * c.W;
                z += m.m20 * c.X + m.m21 * c.Y + m.m22 * c.Z + m.m23 * c.W;
            }
            return new WieldedMechanism.Point(x, y, z);
        }

        private readonly struct LimbTransportFrame
        {
            private static readonly WieldedMechanism.Point Up = new(0, 1, 0);
            private readonly WieldedMechanism.Point _before, _after, _beforeSide, _afterSide;
            internal LimbTransportFrame(WieldedMechanism.Point before, WieldedMechanism.Point after)
            {
                _before = before;
                _after = after;
                _beforeSide = before.Cross(Up);
                _afterSide = after.Cross(Up);
            }
            internal WieldedMechanism.Point Coordinates(Vector3 value)
            {
                var p = WieldedMechanism.Point.From(value);
                return new WieldedMechanism.Point(p.Dot(_before), p.Y, p.Dot(_beforeSide));
            }
            internal Vector3 Transform(WieldedMechanism.Point coordinates) =>
                (_after * coordinates.X + Up * coordinates.Y + _afterSide * coordinates.Z).Float;
        }

        private bool UpdateMechanism()
        {
            var data = Definition?.Mechanism;
            if (data == null) return true;
            _mechanismReady = false;
            if (!Model || !PrepareMechanism(data)) return false;
            for (var i = 0; i < _weaponLocalPaths.Length; i++)
            {
                var relative = Matrix4x4.identity;
                foreach (var bone in _weaponLocalPaths[i])
                    relative *= Matrix4x4.TRS(bone.localPosition, bone.localRotation, bone.localScale);
                _weaponRelativeMatrices[i] = relative;
            }
            var rope = WieldedMechanism.Point.From(_weaponBones[1].localPosition);
            var q = (rope - data.RopeLoaded).Dot(data.RopeAxis) / data.RopeAxis.Dot(data.RopeAxis);
            var span = (ObserveSection(data.LeftObserver) - ObserveSection(data.RightObserver)).Length;
            if (!_mechanismGeometryReady || Math.Abs(q - _mechanismQ) > 0.000001 ||
                Math.Abs(span - _mechanismSpan) > 0.000001)
            {
                if (!data.TryGeometry(q, span, _centers, out var nodes))
                {
                    MechanismStatus = "UnsupportedNativeGeometry";
                    return false;
                }
                _currentLimbNormals ??= new WieldedMechanism.Point[2 * (data.Segments + 1)];
                _limbTransportFrames ??= new LimbTransportFrame[_currentLimbNormals.Length];
                if (_loadedLimbNormals == null)
                {
                    _loadedLimbNormals = new WieldedMechanism.Point[_currentLimbNormals.Length];
                    for (var side = 0; side < 2; side++)
                    for (var section = 0; section <= data.Segments; section++)
                        _loadedLimbNormals[side * (data.Segments + 1) + section] =
                            data.RingNormal(_loadedCenters, section, side == 0);
                }
                for (var side = 0; side < 2; side++)
                for (var section = 0; section <= data.Segments; section++)
                {
                    var index = side * (data.Segments + 1) + section;
                    _currentLimbNormals[index] = data.RingNormal(_centers, section, side == 0);
                    _limbTransportFrames[index] = new LimbTransportFrame(_loadedLimbNormals[index], _currentLimbNormals[index]);
                }
                if (_limbNormalCoordinates == null)
                {
                    _limbNormalCoordinates = new WieldedMechanism.Point[data.LimbRows.Count];
                    _limbTangentCoordinates = new WieldedMechanism.Point[data.LimbRows.Count];
                    _limbPositionValues = new Vector3[data.LimbPositions.Count];
                    for (var i = 0; i < data.LimbRows.Count; i++)
                    {
                        var row = data.LimbRows[i];
                        var section = (row.Left ? 0 : data.Segments + 1) + row.Section;
                        var frame = _limbTransportFrames[section];
                        _limbNormalCoordinates[i] = frame.Coordinates(_frameBaseNormals[row.Index]);
                        _limbTangentCoordinates[i] = frame.Coordinates(_frameBaseTangents[row.Index]);
                    }
                }
                for (var i = 0; i < data.LimbPositions.Count; i++)
                {
                    var position = data.LimbPositions[i];
                    var center = _centers[position.Section];
                    if (position.Left) center = new WieldedMechanism.Point(-center.X, center.Y, center.Z);
                    var section = (position.Left ? 0 : data.Segments + 1) + position.Section;
                    _limbPositionValues[i] = (center + new WieldedMechanism.Point(0, position.Height, 0) +
                        _currentLimbNormals[section] * position.Distance).Float;
                }
                for (var i = 0; i < data.LimbRows.Count; i++)
                {
                    var row = data.LimbRows[i];
                    var section = (row.Left ? 0 : data.Segments + 1) + row.Section;
                    _frameVertices[row.Index] = _limbPositionValues[data.LimbPositionIndices[i]];
                    _frameNormals[row.Index] = _limbTransportFrames[section].Transform(_limbNormalCoordinates[i]);
                    var t = _limbTransportFrames[section].Transform(_limbTangentCoordinates[i]);
                    _frameTangents[row.Index] = new Vector4(t.x, t.y, t.z, _frameBaseTangents[row.Index].w);
                }
                if (!_mechanismGeometryReady || q != _mechanismQ)
                {
                    WieldedMechanism.Joint joint = null;
                    double cosine = 1, sine = 0;
                    foreach (var row in data.RigidRows)
                    {
                        if (!ReferenceEquals(joint, row.Joint))
                        {
                            joint = row.Joint;
                            var angle = joint.Angle(q);
                            cosine = Math.Cos(angle);
                            sine = Math.Sin(angle);
                        }
                        _frameVertices[row.Index] = (joint.Pivot + joint.Rotate(row.Position - joint.Pivot, cosine, sine)).Float;
                        _frameNormals[row.Index] = joint.Rotate(WieldedMechanism.Point.From(_frameBaseNormals[row.Index]), cosine, sine).Float;
                        var t = joint.Rotate(WieldedMechanism.Point.From(_frameBaseTangents[row.Index]), cosine, sine).Float;
                        _frameTangents[row.Index] = new Vector4(t.x, t.y, t.z, _frameBaseTangents[row.Index].w);
                    }
                }
                if (_loadedStringFrames == null)
                {
                    _loadedStringFrames = new (WieldedMechanism.Point, WieldedMechanism.Point, WieldedMechanism.Point)
                        [_loadedStringNodes.Length];
                    _currentStringFrames = new (WieldedMechanism.Point, WieldedMechanism.Point, WieldedMechanism.Point)
                        [_loadedStringNodes.Length];
                    for (var ring = 0; ring < _loadedStringFrames.Length; ring++)
                        _loadedStringFrames[ring] = WieldedMechanism.StringFrame(_loadedStringNodes, ring);
                    _stringNormalCoordinates = new WieldedMechanism.Point[data.StringIndices.Count];
                    _stringTangentCoordinates = new WieldedMechanism.Point[data.StringIndices.Count];
                    for (var i = 0; i < data.StringIndices.Count; i++)
                    {
                        var loaded = _loadedStringFrames[data.StringRows[data.StringIndices[i]].Ring];
                        var n = WieldedMechanism.Point.From(_stringBaseNormals[i]);
                        _stringNormalCoordinates[i] = new WieldedMechanism.Point(n.Dot(loaded.Tangent),
                            n.Dot(loaded.Up), n.Dot(loaded.Side));
                        var t = WieldedMechanism.Point.From(_stringBaseTangents[i]);
                        _stringTangentCoordinates[i] = new WieldedMechanism.Point(t.Dot(loaded.Tangent),
                            t.Dot(loaded.Up), t.Dot(loaded.Side));
                    }
                }
                for (var ring = 0; ring < _currentStringFrames.Length; ring++)
                    _currentStringFrames[ring] = WieldedMechanism.StringFrame(nodes, ring);
                for (var i = 0; i < data.StringIndices.Count; i++)
                {
                    var row = data.StringRows[data.StringIndices[i]];
                    var basis = _currentStringFrames[row.Ring];
                    _stringVertices[i] = (nodes[row.Ring] + basis.Tangent * row.Offset.X +
                        basis.Up * row.Offset.Y + basis.Side * row.Offset.Z).Float;
                    var n = _stringNormalCoordinates[i];
                    _stringNormals[i] = (basis.Tangent * n.X + basis.Up * n.Y + basis.Side * n.Z).Float;
                    var t = _stringTangentCoordinates[i];
                    var moved = (basis.Tangent * t.X + basis.Up * t.Y + basis.Side * t.Z).Float;
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
                _mechanismQ = q;
                _mechanismSpan = span;
                _mechanismGeometryReady = true;
            }
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
            _weaponLocalPaths = null;
            _weaponRelativeMatrices = null;
            _loadedLimbNormals = _currentLimbNormals = null;
            _limbTransportFrames = null;
            _limbPositionValues = null;
            _limbNormalCoordinates = _limbTangentCoordinates = null;
            _stringNormalCoordinates = _stringTangentCoordinates = null;
            _loadedStringFrames = _currentStringFrames = null;
            _mechanismGeometryReady = false;
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
            var fingers = ClosedHandReference(source);
            if (!fingers) return false;

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
                _fingerReference = fingers;
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

        private static AnimationClip ClosedHandReference(RuntimeAnimatorController source)
        {
            AnimationClip selected = null;
            foreach (var clip in source.animationClips)
            {
                if (!clip || clip.name != "ClosedHands") continue;
                if (selected && selected != clip) return null;
                selected = clip;
            }
            return selected && selected.isHumanMotion ? selected : null;
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
            _gripReady = false;
            _modelNormalSign = 0f;
            _nativeGripAxisSign = 0f;
            _armSkins = null;
            _skinShapes = null;
            _refreshArmBindings = false;
            InvalidateSkinMeasurements();
            _armInParent = null;
            _plateRoll = 0f;
            _plateFitPoints = null;
            PlateClearanceGap = 0f;
            GripStatus = "Unbound";
            _fingerBones = null;
            _fingerTargets = null;
            _fingerReference = null;
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

    internal static string TextureRgba32LinearFingerprint(int width, int height, byte[] rgba, int count)
    {
        if (rgba == null || count != checked(width * height * 4) || count > rgba.Length)
            throw new InvalidDataException("A native texture fingerprint has an incomplete RGBA32 payload.");
        using var sha = CreateAssetSha256();
        using var header = new MemoryStream();
        using var writer = new BinaryWriter(header, System.Text.Encoding.UTF8, true);
        writer.Write("Solasta.TextureRgba32Linear.1"); writer.Write(width); writer.Write(height); writer.Flush();
        sha.TransformBlock(header.GetBuffer(), 0, (int)header.Length, null, 0);
        sha.TransformBlock(rgba, 0, count, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
    }

    internal static SHA256 CreateAssetSha256() => Environment.OSVersion.Platform == PlatformID.Win32NT
        ? new WindowsAssetSha256()
        : SHA256.Create();

    private sealed class WindowsAssetSha256 : SHA256
    {
        private AlgorithmHandle _algorithm;
        private HashHandle _hash;

        internal WindowsAssetSha256()
        {
            HashSizeValue = 256;
            try
            {
                Check(BCryptOpenAlgorithmProvider(out _algorithm, "SHA256", null, 0));
                Initialize();
            }
            catch
            {
                _algorithm?.Dispose();
                throw;
            }
        }

        public override void Initialize()
        {
            if (_algorithm == null || _algorithm.IsClosed)
                throw new ObjectDisposedException(nameof(WindowsAssetSha256));
            _hash?.Dispose();
            _hash = null;
            HashHandle next = null;
            try
            {
                Check(BCryptCreateHash(_algorithm, out next, IntPtr.Zero, 0, IntPtr.Zero, 0, 0));
                next.RetainAlgorithm(_algorithm);
                _hash = next;
            }
            catch
            {
                next?.Dispose();
                throw;
            }
        }

        protected override void HashCore(byte[] array, int offset, int count)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            if (offset < 0 || count < 0 || offset > array.Length - count)
                throw new ArgumentOutOfRangeException();
            if (count == 0) return;
            var pinned = GCHandle.Alloc(array, GCHandleType.Pinned);
            try
            {
                Check(BCryptHashData(_hash, IntPtr.Add(pinned.AddrOfPinnedObject(), offset), count, 0));
            }
            finally
            {
                pinned.Free();
            }
        }

        protected override byte[] HashFinal()
        {
            var result = new byte[32];
            Check(BCryptFinishHash(_hash, result, result.Length, 0));
            return result;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash?.Dispose();
                _hash = null;
                _algorithm?.Dispose();
                _algorithm = null;
            }
            base.Dispose(disposing);
        }

        private static void Check(int status)
        {
            if (status < 0) throw new CryptographicException(status);
        }

        private sealed class AlgorithmHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private AlgorithmHandle() : base(true) { }
            protected override bool ReleaseHandle() => BCryptCloseAlgorithmProvider(handle, 0) >= 0;
        }

        private sealed class HashHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private AlgorithmHandle _parent;
            private HashHandle() : base(true) { }

            internal void RetainAlgorithm(AlgorithmHandle algorithm)
            {
                var retained = false;
                try
                {
                    algorithm.DangerousAddRef(ref retained);
                    _parent = algorithm;
                }
                catch
                {
                    if (retained) algorithm.DangerousRelease();
                    throw;
                }
            }

            protected override bool ReleaseHandle()
            {
                try
                {
                    return BCryptDestroyHash(handle) >= 0;
                }
                finally
                {
                    _parent?.DangerousRelease();
                    _parent = null;
                }
            }
        }

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int BCryptOpenAlgorithmProvider(out AlgorithmHandle algorithm, string name,
            string implementation, int flags);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, int flags);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptCreateHash(AlgorithmHandle algorithm, out HashHandle hash, IntPtr state,
            int stateLength, IntPtr secret, int secretLength, int flags);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptHashData(HashHandle hash, IntPtr data, int count, int flags);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptFinishHash(HashHandle hash, [Out] byte[] output, int count, int flags);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptDestroyHash(IntPtr hash);
    }

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

        private static (string File, string Hash)[] ReadAssetPacks(string name, string kind, string directory)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name != Path.GetFileName(name) || name is "." or "..")
                throw new InvalidDataException("Invalid asset library name.");
            var manifest = Path.Combine(directory, name + "." + kind + ".json");
            if (!File.Exists(manifest)) return new[] { (name + "." + kind + ".bundle", (string)null) };
            using var text = File.OpenText(manifest);
            using var reader = new JsonTextReader(text)
                { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double };
            var data = JObject.Load(reader,
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read() || data["schemaVersion"]?.Type != JTokenType.Integer ||
                (long?)data["schemaVersion"] != 1 || data["packs"] is not JArray packs || packs.Count == 0)
                throw new InvalidDataException("Invalid asset library manifest.");
            var names = new List<(string File, string Hash)>();
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in packs)
            {
                if (token is not JObject pack || pack["file"]?.Type != JTokenType.String ||
                    pack["sha256"]?.Type != JTokenType.String)
                    throw new InvalidDataException("Invalid asset library pack.");
                var file = pack["file"].Value<string>();
                var hash = pack["sha256"].Value<string>();
                if (string.IsNullOrWhiteSpace(file) || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    file != Path.GetFileName(file) ||
                    file.IndexOf('/') >= 0 || file.IndexOf('\\') >= 0 ||
                    !file.EndsWith("." + kind + ".bundle", StringComparison.Ordinal) || !files.Add(file) ||
                    hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
                    throw new InvalidDataException("Invalid asset library packs.");
                names.Add((file, hash));
            }
            return names.ToArray();
        }

        private T[] LoadAssets<T>(string name, string kind, Dictionary<string, T[]> cache) where T : UnityEngine.Object
        {
            if (cache.TryGetValue(name, out var cached)) return cached;
            var bundles = new List<AssetBundle>();
            T[] assets;
            try
            {
                var directory = Path.Combine(Main.ModFolder, "BlenderModels", name);
                var names = ReadAssetPacks(name, kind, directory);
                var result = new List<T>();
                var assetNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pack in names)
                {
                    var path = Path.Combine(directory, pack.File);
                    if (pack.Hash != null)
                    {
                        using var algorithm = CreateAssetSha256();
                        using var stream = File.OpenRead(path);
                        var actualHash = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "");
                        if (!string.Equals(actualHash, pack.Hash, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Asset pack hash mismatch: " + path);
                    }
                    var bundle = File.Exists(path) ? AssetBundle.LoadFromFile(path) : null;
                    if (!bundle) throw new InvalidDataException("Failed to load asset pack: " + path);
                    bundles.Add(bundle);
                    var loaded = bundle.LoadAllAssets<T>();
                    if (loaded.Length == 0) throw new InvalidDataException("Empty asset pack: " + path);
                    foreach (var asset in loaded)
                    {
                        if (!asset || string.IsNullOrWhiteSpace(asset.name) || !assetNames.Add(asset.name))
                            throw new InvalidDataException("Duplicate or invalid library asset: " + path);
                        result.Add(asset);
                    }
                }
                assets = result.ToArray();
                _assetBundles.AddRange(bundles);
                bundles.Clear();
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                ArgumentException or InvalidOperationException or OverflowException or InvalidCastException)
            {
                Main.Error($"Failed to load {kind} library {name}: {exception.Message}");
                assets = [];
            }
            finally
            {
                foreach (var bundle in bundles)
                    if (bundle) bundle.Unload(true);
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
            PrefabObjectHashes[prefabGuid] = obj.ObjectSourceSha256;
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
        internal const int CurrentSchemaVersion = 3;

        [OptionalField] public int SchemaVersion = CurrentSchemaVersion;
        [OptionalField] public string ObjectSourceSha256;
        [OptionalField] public string MaterialSourceSha256;

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

        var pathObjFile = Path.Combine(pathModel, $"{filename}.obj");
        var pathMtlFile = Path.Combine(pathModel, $"{filename}.mtl");
        var pathCacheFile = Path.Combine(pathCache, $"{filename}.cache");

        try
        {
            // Hash and parse the same source bytes. Package rollback and file
            // copies can retain timestamps while changing their contents.
            var objectSource = File.ReadAllBytes(pathObjFile);
            byte[] materialSource;
            try
            {
                materialSource = File.ReadAllBytes(pathMtlFile);
            }
            catch
            {
                Main.Error($"Failed to read material file {pathMtlFile}");
                return false;
            }
            using (var algorithm = CreateAssetSha256())
            {
                objFile.ObjectSourceSha256 = BitConverter.ToString(algorithm.ComputeHash(objectSource)).Replace("-", "");
                objFile.MaterialSourceSha256 = BitConverter.ToString(algorithm.ComputeHash(materialSource)).Replace("-", "");
            }
            if (File.Exists(pathCacheFile) &&
                Deserialize(pathCacheFile, objFile.ObjectSourceSha256, objFile.MaterialSourceSha256, ref objFile))
            {
                Main.Info($"Read {filename} from cache.");
                return true;
            }

            var lines = ReadSourceLines(objectSource);

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

            if (!TryReadMaterialFile(pathMtlFile, materialSource, objFile.Materials))
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

    private static IEnumerable<string> ReadSourceLines(byte[] source)
    {
        using var stream = new MemoryStream(source, false);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static bool TryReadMaterialFile(string pathMtl, byte[] source, List<NewMtl> materials)
    {
        try
        {
            var lines = ReadSourceLines(source);

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
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var file = File.Create(path);
            var bf = GetBinaryFormatter();

            bf.Serialize(file, objFile);
        }
        catch (Exception e)
        {
            Main.Error($"Failed to serialize {path} {e.Message}");
        }
    }

    private static bool Deserialize(string path, string objectSourceSha256, string materialSourceSha256,
        ref ObjectFile objFile)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open);
            var bf = GetBinaryFormatter();

            var cached = (ObjectFile)bf.Deserialize(file);
            if (cached.SchemaVersion != ObjectFile.CurrentSchemaVersion ||
                !string.Equals(cached.ObjectSourceSha256, objectSourceSha256, StringComparison.Ordinal) ||
                !string.Equals(cached.MaterialSourceSha256, materialSourceSha256, StringComparison.Ordinal))
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
