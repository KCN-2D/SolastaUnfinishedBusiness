using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.Models;

internal sealed class FaceAgeColorSurface(Material skin) : IDisposable
{
    internal static readonly int MainTexture = Shader.PropertyToID("_MainTex");
    private static readonly int FaceColor = Shader.PropertyToID("_FaceColor");
    private static readonly int FaceColorScale = Shader.PropertyToID("_FaceColor_ST");
    private static readonly int FaceAnatomy = Shader.PropertyToID("_FaceAnatomy");
    private static readonly int FaceLandmarks = Shader.PropertyToID("_FaceLandmarks");
    private static readonly int FaceAge = Shader.PropertyToID("_FaceAge");
    private static Material _paint;

    private readonly MaterialPropertyBlock _properties = new();
    private Texture2D _source;
    private RenderTexture _texture;
    private Mesh _mesh;
    private int _slot;
    private float _amount = -1f;
    private Vector4 _transform;

    private static Material PaintMaterial()
    {
        if (_paint) return _paint;
        var path = Path.Combine(Main.ModFolder, "BlenderModels", "FaceAge", "face_age.materials.bundle");
        var bundle = AssetBundle.LoadFromFile(path);
        if (!bundle) throw new InvalidDataException("The face aging material bundle could not be opened.");
        try
        {
            var material = bundle.LoadAllAssets<Material>().SingleOrDefault();
            if (!material || !material.shader.isSupported || material.FindPass("FACE_AGE_COLOR") != 0)
                throw new InvalidDataException("The face aging material does not provide its color pass.");
            _paint = material;
            return _paint;
        }
        finally { bundle.Unload(!_paint); }
    }

    internal void Apply(Mesh mesh, int slot, Matrix4x4 frame, Vector4 landmarks, float amount)
    {
        var current = skin.GetTexture(MainTexture);
        if (current != _texture && current != _source)
        {
            Dispose();
            _source = current as Texture2D;
        }
        if (!_source) return;

        var scale = skin.GetTextureScale(MainTexture);
        var offset = skin.GetTextureOffset(MainTexture);
        var transform = new Vector4(scale.x, scale.y, offset.x, offset.y);
        if (!_texture || !_texture.IsCreated() || _amount != amount || _transform != transform ||
            _mesh != mesh || _slot != slot)
        {
            var paint = PaintMaterial();
            if (!_texture)
                _texture = new RenderTexture(_source.width, _source.height, 0, RenderTextureFormat.ARGB32)
                {
                    name = _source.name + " (Age)",
                    wrapMode = _source.wrapMode,
                    filterMode = _source.filterMode,
                    anisoLevel = _source.anisoLevel,
                    mipMapBias = _source.mipMapBias,
                    useMipMap = _source.mipmapCount > 1,
                    autoGenerateMips = false
                };
            if (!_texture.IsCreated() && !_texture.Create()) return;

            _properties.SetTexture(FaceColor, _source);
            _properties.SetVector(FaceColorScale, transform);
            _properties.SetMatrix(FaceAnatomy, frame);
            _properties.SetVector(FaceLandmarks, landmarks);
            _properties.SetFloat(FaceAge, amount);
            using var commands = new CommandBuffer { name = "Character face age color" };
            commands.SetRenderTarget(_texture);
            commands.DrawMesh(mesh, Matrix4x4.identity, paint, slot, 0, _properties);

            var active = RenderTexture.active;
            var srgb = GL.sRGBWrite;
            try
            {
                GL.sRGBWrite = _texture.sRGB;
                Graphics.Blit(_source, _texture);
                Graphics.ExecuteCommandBuffer(commands);
                if (_texture.useMipMap) _texture.GenerateMips();
            }
            finally
            {
                GL.sRGBWrite = srgb;
                RenderTexture.active = active;
            }
            _amount = amount;
            _mesh = mesh;
            _slot = slot;
            _transform = transform;
        }
        skin.SetTexture(MainTexture, _texture);
    }

    internal void Restore()
    {
        if (skin && _texture && skin.GetTexture(MainTexture) == _texture)
            skin.SetTexture(MainTexture, _source);
    }

    public void Dispose()
    {
        Restore();
        if (_texture)
        {
            _texture.Release();
            Object.Destroy(_texture);
        }
        _source = null;
        _texture = null;
        _mesh = null;
        _amount = -1f;
        _properties.Clear();
    }
}
