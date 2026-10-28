using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public static class PresentationCaptureProbe
{
    public static IEnumerator Run(Action<string> log)
    {
        var owner = new GameObject("Owned capture contract");
        var material = new Material(Shader.Find("Unlit/Color"));
        var mesh = new Mesh();
        try
        {
            owner.transform.position = new Vector3(1000, 1000, 1000);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(owner.transform, false);
            cube.layer = 30;
            var renderer = cube.GetComponent<Renderer>();
            material.color = Color.cyan;
            renderer.sharedMaterial = material;
            var cameraObject = new GameObject("Owned capture camera");
            cameraObject.transform.SetParent(owner.transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.orthographic = true;
            camera.orthographicSize = 1;
            camera.aspect = 1f;
            camera.cullingMask = 1 << 30;
            camera.transform.localPosition = new Vector3(0, 0, -4);
            camera.nearClipPlane = .02f;
            camera.farClipPlane = 12f;
            camera.transform.localPosition = new Vector3(0, 0, -60);
            var originalRotation = camera.transform.rotation;
            PresentationCapture.FrameSubjects(camera, new[] { renderer });
            Check(log, AllCornersVisible(camera, renderer.bounds) && camera.nearClipPlane == .02f &&
                camera.farClipPlane == 12f && camera.transform.rotation == originalRotation,
                "surface framing recovers out-of-depth camera within unchanged clip planes");
            var framedPosition = camera.transform.position;
            var framedSize = camera.orthographicSize;
            var effectObject = new GameObject("Owned non-surface framing boundary");
            effectObject.layer = 30;
            effectObject.transform.SetParent(owner.transform, false);
            effectObject.transform.localPosition = Vector3.forward * -100;
            var effect = effectObject.AddComponent<ParticleSystem>();
            effect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            PresentationCapture.FrameSubjects(camera, new[] { renderer, effect.GetComponent<ParticleSystemRenderer>() });
            Check(log, camera.transform.position == framedPosition && camera.orthographicSize == framedSize &&
                AllCornersVisible(camera, renderer.bounds), "non-surface renderers cannot change surface framing");
            effectObject.SetActive(false);
            UnityEngine.Object.Destroy(effectObject);
            PresentationCapture.Receipt receipt = null;
            yield return PresentationCapture.Capture(camera, new[] { renderer }, "capture-contract", 128, 128,
                result => receipt = result);
            Check(log, receipt != null && receipt.accepted && receipt.capturedFrame >= receipt.requestFrame + 2 &&
                receipt.variedPixels > 0 && receipt.sha256.Length == 64 && receipt.subjects.Length == 1 &&
                receipt.subjects[0].includedLayer && receipt.subjects[0].inFrustum &&
                !camera.enabled && camera.targetTexture == null, "settled render, native receipt and camera restoration");
            Check(log, File.Exists(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory,
                "capture-contract.capture.json")), "camera receipt is stored beside the original PNG");

            var callbackOrder = new System.Collections.Generic.List<string>();
            var beforeCalls = 0;
            var capturedCalls = 0;
            var renderToken = false;
            var naturalRenders = 0;
            var finalRenders = 0;
            var renderHook = cameraObject.AddComponent<PresentationCaptureContractRenderHook>();
            renderHook.BeforeCull = () => {
                if (renderToken) { callbackOrder.Add("render"); finalRenders++; renderToken = false; }
                else naturalRenders++;
            };
            yield return PresentationCapture.Capture(camera, new[] { renderer }, "capture-callback-order", 128, 128,
                result => receipt = result, 2, 2,
                () => { capturedCalls++; callbackOrder.Add("captured"); },
                () => { beforeCalls++; callbackOrder.Add("before"); renderToken = true; });
            Check(log, receipt != null && receipt.accepted && beforeCalls == 1 && capturedCalls == 1 &&
                finalRenders == 1 && naturalRenders >= 2 && !renderToken &&
                callbackOrder.SequenceEqual(new[] { "before", "render", "captured" }),
                "render callbacks run once in order around the final manual draw after natural warmup");
            var previousActive = RenderTexture.active;
            beforeCalls = 0; capturedCalls = 0; finalRenders = 0;
            yield return ExpectRejected(PresentationCapture.Capture(camera, new[] { renderer },
                "capture-before-callback-failure", 128, 128, result => receipt = result, 2, 2,
                () => capturedCalls++, () => { beforeCalls++; throw new InvalidOperationException("Owned before-render failure"); }),
                log, "before-render callback failure remains a rejected capture");
            Check(log, beforeCalls == 1 && capturedCalls == 0 && finalRenders == 0 && !receipt.accepted &&
                !camera.enabled && camera.targetTexture == null && RenderTexture.active == previousActive,
                "before-render failure restores camera and render target without a final draw");
            yield return ExpectRejected(PresentationCapture.Capture(camera, new[] { renderer },
                "capture-after-callback-failure", 128, 128, result => receipt = result, 2, 0,
                () => { throw new InvalidOperationException("Owned after-render failure"); },
                () => { beforeCalls++; renderToken = true; }),
                log, "after-render callback failure remains a rejected capture");
            Check(log, finalRenders == 1 && !renderToken && !receipt.accepted &&
                !camera.enabled && camera.targetTexture == null && RenderTexture.active == previousActive,
                "after-render failure restores camera and render target after its actual final draw");
            renderHook.BeforeCull = null;

            var rejected = false;
            try { PresentationCapture.Capture(camera, new[] { renderer }, "../invalid", 128, 128).MoveNext(); }
            catch (ArgumentException) { rejected = true; }
            Check(log, rejected, "capture traversal is rejected before rendering");
            var threadCheck = Task.Run(() => {
                try { PresentationCapture.Capture(camera, new[] { renderer }, "capture-worker", 128, 128).MoveNext(); }
                catch (InvalidOperationException) { return true; }
                return false;
            });
            while (!threadCheck.IsCompleted) yield return null;
            Check(log, threadCheck.Result, "worker thread is rejected before accessing Unity objects");
            camera.cullingMask = 0;
            yield return ExpectRejected(PresentationCapture.Capture(camera, new[] { renderer },
                "capture-no-subject", 128, 128), log, "invisible subjects cannot produce accepted capture evidence");
            Check(log, camera.cullingMask == 0 && camera.targetTexture == null && !camera.enabled,
                "failed capture preserves caller camera state");
            camera.cullingMask = 1 << 30;
            material.color = Color.black;
            yield return ExpectRejected(PresentationCapture.Capture(camera, new[] { renderer },
                "capture-uniform", 128, 128), log, "uniform framebuffer is retained and rejected");

            var triangleObject = new GameObject("Owned contact triangle");
            triangleObject.transform.SetParent(owner.transform, false);
            var bone = new GameObject("Owned contact bone").transform;
            bone.SetParent(triangleObject.transform, false);
            mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(0, 1, 0) };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.boneWeights = Enumerable.Range(0, 3).Select(index => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
            mesh.RecalculateBounds();
            var skin = triangleObject.AddComponent<SkinnedMeshRenderer>();
            skin.sharedMesh = mesh; skin.bones = new[] { bone }; skin.rootBone = bone;
            var separated = PresentationCapture.MeasureSphereSurface(new[] { skin },
                owner.transform.position + Vector3.forward, .25f, new[] { bone });
            var intersected = PresentationCapture.MeasureSphereSurface(new[] { skin },
                owner.transform.position + Vector3.forward * .2f, .25f, new[] { bone });
            Check(log, separated.triangles == 1 && Mathf.Abs(separated.surfaceGap - .75f) < .0001f &&
                separated.intersectingTriangles == 0 && Mathf.Abs(intersected.surfaceGap + .05f) < .0001f &&
                intersected.intersectingTriangles == 1, "baked triangle surface distance detects separation and intersection");
            owner.transform.localScale = Vector3.one * .6553f;
            owner.transform.rotation = Quaternion.Euler(15, 29, -12);
            triangleObject.transform.localScale = Vector3.one * 1.4f;
            triangleObject.transform.localRotation = Quaternion.Euler(-8, 17, 23);
            bone.localPosition = new Vector3(.2f, .3f, .4f);
            using (var baked = new OwnedMesh())
            {
                skin.BakeMesh(baked.Value);
                var worldWithScale = 0f;
                var worldWithoutScale = 0f;
                var rigid = Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one);
                for (var index = 0; index < mesh.vertexCount; index++)
                {
                    var expected = bone.TransformPoint(mesh.vertices[index]);
                    worldWithScale = Mathf.Max(worldWithScale,
                        Vector3.Distance(skin.transform.TransformPoint(baked.Value.vertices[index]), expected));
                    worldWithoutScale = Mathf.Max(worldWithoutScale,
                        Vector3.Distance(rigid.MultiplyPoint3x4(baked.Value.vertices[index]), expected));
                }
                log("INFO: Native BakeMesh scaled triangle: lossyScale=" + skin.transform.lossyScale.ToString("F6") +
                    "; TransformPoint error=" + worldWithScale.ToString("R") +
                    "; rotation/translation error=" + worldWithoutScale.ToString("R"));
                Check(log, Mathf.Min(worldWithScale, worldWithoutScale) < .0003f,
                    "scaled BakeMesh output has an independently known world-space mapping");
            }
            var normal = skin.transform.forward;
            var scaledContact = PresentationCapture.MeasureSphereSurface(new[] { skin },
                bone.position + normal, .25f, new[] { bone });
            Check(log, Mathf.Abs(scaledContact.surfaceGap - .75f) < .0003f,
                "scaled and rotated baked contact preserves known world surface distance");
            var nonFiniteRejected = false;
            try { PresentationCapture.MeasureSphereSurface(new[] { skin }, new Vector3(float.NaN, 0, 0), .25f); }
            catch (ArgumentException) { nonFiniteRejected = true; }
            Check(log, nonFiniteRejected, "non-finite coordinates are rejected before skin measurement");
            Check(log, PresentationCapture.ClosestPoint(new Vector3(.2f, .3f, 1),
                Vector3.zero, Vector3.right, Vector3.up) == new Vector3(.2f, .3f, 0) &&
                PresentationCapture.ClosestPoint(Vector3.right, Vector3.zero, Vector3.zero, Vector3.zero) == Vector3.zero,
                "triangle interior and degenerate faces have finite closest points");
        }
        finally
        {
            owner.SetActive(false);
            UnityEngine.Object.Destroy(owner);
            UnityEngine.Object.Destroy(material);
            UnityEngine.Object.Destroy(mesh);
        }
    }

    private sealed class OwnedMesh : IDisposable
    {
        public readonly Mesh Value = new Mesh();
        public void Dispose() { UnityEngine.Object.Destroy(Value); }
    }

    private static bool AllCornersVisible(Camera camera, Bounds bounds)
    {
        for (var x = -1; x <= 1; x += 2)
        for (var y = -1; y <= 1; y += 2)
        for (var z = -1; z <= 1; z += 2)
        {
            var point = camera.WorldToViewportPoint(bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z)));
            if (point.x < 0 || point.x > 1 || point.y < 0 || point.y > 1 ||
                point.z <= camera.nearClipPlane || point.z >= camera.farClipPlane) return false;
        }
        return true;
    }

    private static IEnumerator ExpectRejected(IEnumerator routine, Action<string> log, string label)
    {
        var rejected = false;
        try
        {
            while (true)
            {
                object step = null;
                bool next = false;
                try { next = routine.MoveNext(); if (next) step = routine.Current; }
                catch (InvalidOperationException) { rejected = true; }
                if (rejected || !next) break;
                yield return step;
            }
        }
        finally { (routine as IDisposable)?.Dispose(); }
        Check(log, rejected, label);
    }

    private static void Check(Action<string> log, bool actual, string label)
    {
        log((actual ? "PASS: " : "FAIL: ") + label);
        if (!actual) throw new InvalidOperationException(label);
    }
}

public sealed class PresentationCaptureContractRenderHook : MonoBehaviour
{
    public Action BeforeCull;
    void OnPreCull() { BeforeCull?.Invoke(); }
}
