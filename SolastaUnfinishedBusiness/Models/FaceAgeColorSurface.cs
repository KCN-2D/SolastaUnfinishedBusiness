using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.Models;

internal sealed class FaceAgeColorSurface(Material skin, Material paint) : IDisposable
{
    internal const string MaterialKey = "FaceAgeColor";
    internal static readonly int MainTexture = Shader.PropertyToID("_MainTex");
    private static readonly int FaceColor = Shader.PropertyToID("_FaceColor");
    private static readonly int FaceColorScale = Shader.PropertyToID("_FaceColor_ST");
    private static readonly int FaceAnatomy = Shader.PropertyToID("_FaceAnatomy");
    private static readonly int FaceLandmarks = Shader.PropertyToID("_FaceLandmarks");
    private static readonly int FaceAge = Shader.PropertyToID("_FaceAge");
    private static readonly int RepairPigment = Shader.PropertyToID("_RepairPigment");

    private readonly MaterialPropertyBlock _properties = new();
    private readonly int _paintPass = paint.FindPass("FACE_AGE_COLOR");
    private Texture2D _source;
    private bool _repairPigment;
    private RenderTexture _aged;
    private RenderTexture _applied;
    private Mesh _mesh;
    private int _slot;
    private float _amount = -1f;
    private Vector4 _transform;

    internal void Apply(Mesh mesh, int slot, Matrix4x4 frame, Vector4 landmarks, bool repairPigment, float amount,
        Func<string, Func<Object[]>, Object[]> retainNeutral)
    {
        var current = skin.GetTexture(MainTexture);
        if (current != _applied && current != _source)
        {
            Dispose();
            _source = current as Texture2D;
        }
        if (!_source) return;

        var scale = skin.GetTextureScale(MainTexture);
        var offset = skin.GetTextureOffset(MainTexture);
        var transform = new Vector4(scale.x, scale.y, offset.x, offset.y);
        if (amount == 0f)
        {
            if (!repairPigment) return;
            var key = FormattableString.Invariant($"face-pigment:{mesh.GetInstanceID()}:{slot}:{_source.GetInstanceID()}:{transform.x:R}:{transform.y:R}:{transform.z:R}:{transform.w:R}");
            var neutral = (RenderTexture)retainNeutral(key, () =>
            {
                var created = NewTexture(" (Pigment)");
                try
                {
                    Paint(created, mesh, slot, frame, landmarks, transform, true, 0f);
                    return [created];
                }
                catch
                {
                    Object.Destroy(created);
                    throw;
                }
            })[0];
            if (!neutral.IsCreated()) Paint(neutral, mesh, slot, frame, landmarks, transform, true, 0f);
            _applied = neutral;
        }
        else
        {
            if (!_aged) _aged = NewTexture(" (Age)");
            if (!_aged.IsCreated() || _amount != amount || _transform != transform ||
                _mesh != mesh || _slot != slot || _repairPigment != repairPigment)
            {
                Paint(_aged, mesh, slot, frame, landmarks, transform, repairPigment, amount);
                _amount = amount;
                _mesh = mesh;
                _slot = slot;
                _repairPigment = repairPigment;
                _transform = transform;
            }
            _applied = _aged;
        }
        if (_applied.IsCreated()) skin.SetTexture(MainTexture, _applied);
    }

    private RenderTexture NewTexture(string suffix) => new(_source.width, _source.height, 0, RenderTextureFormat.ARGB32)
    {
        name = _source.name + suffix,
        wrapMode = _source.wrapMode,
        filterMode = _source.filterMode,
        anisoLevel = _source.anisoLevel,
        mipMapBias = _source.mipMapBias,
        useMipMap = _source.mipmapCount > 1,
        autoGenerateMips = false
    };

    private void Paint(RenderTexture target, Mesh mesh, int slot, Matrix4x4 frame, Vector4 landmarks,
        Vector4 transform, bool repairPigment, float amount)
    {
        if (_paintPass < 0) throw new InvalidOperationException("The face aging material does not provide its color pass.");
        if (!target.IsCreated() && !target.Create()) return;
        _properties.SetTexture(FaceColor, _source);
        _properties.SetVector(FaceColorScale, transform);
        _properties.SetMatrix(FaceAnatomy, frame);
        _properties.SetVector(FaceLandmarks, landmarks);
        _properties.SetFloat(FaceAge, amount);
        _properties.SetFloat(RepairPigment, repairPigment ? 1f : 0f);
        using var commands = new CommandBuffer { name = "Character face color" };
        commands.SetRenderTarget(target);
        commands.DrawMesh(mesh, Matrix4x4.identity, paint, slot, _paintPass, _properties);

        var active = RenderTexture.active;
        var srgb = GL.sRGBWrite;
        try
        {
            GL.sRGBWrite = target.sRGB;
            Graphics.Blit(_source, target);
            Graphics.ExecuteCommandBuffer(commands);
            if (target.useMipMap) target.GenerateMips();
        }
        finally
        {
            GL.sRGBWrite = srgb;
            RenderTexture.active = active;
        }
    }

    internal void Restore()
    {
        if (skin && _applied && skin.GetTexture(MainTexture) == _applied)
            skin.SetTexture(MainTexture, _source);
    }

    public void Dispose()
    {
        Restore();
        if (_aged)
        {
            _aged.Release();
            Object.Destroy(_aged);
        }
        _source = null;
        _repairPigment = false;
        _aged = null;
        _applied = null;
        _mesh = null;
        _amount = -1f;
        _properties.Clear();
    }
}
