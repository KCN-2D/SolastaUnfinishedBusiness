#ifndef UB_NATIVE_HAIR_FAMILY_FX_INCLUDED
#define UB_NATIVE_HAIR_FAMILY_FX_INCLUDED

// Algebra transcribed from the installed native FakeAnisoHair fragment DXBC.
// In particular the native dissolve uses object-space 3-D simplex noise and
// its world-height mask; it is not replaced by a groom texture or UV noise.
float3 SGMod289(float3 x) { return x - floor(x / 289.0) * 289.0; }
float4 SGMod289(float4 x) { return x - floor(x / 289.0) * 289.0; }
float4 SGPermute(float4 x) { return SGMod289(((x * 34.0) + 1.0) * x); }
float4 SGTaylorInverseSqrt(float4 r) { return 1.79284291400159 - 0.85373472095314 * r; }

float SGSimplex(float3 v)
{
    const float2 C = float2(1.0 / 6.0, 1.0 / 3.0);
    float3 i = floor(v + dot(v, C.yyy));
    float3 x0 = v - i + dot(i, C.xxx);
    float3 g = step(x0.yzx, x0.xyz), l = 1 - g;
    float3 i1 = min(g.xyz, l.zxy), i2 = max(g.xyz, l.zxy);
    float3 x1 = x0 - i1 + C.xxx, x2 = x0 - i2 + C.yyy, x3 = x0 - 0.5;
    i = SGMod289(i);
    float4 p = SGPermute(SGPermute(SGPermute(i.z + float4(0, i1.z, i2.z, 1))
                        + i.y + float4(0, i1.y, i2.y, 1)) + i.x + float4(0, i1.x, i2.x, 1));
    float4 j = p - 49 * floor(p / 49);
    float4 x_ = floor(j / 7), y_ = floor(j - 7 * x_);
    float4 x = (x_ * 2 + 0.5) / 7 - 1, y = (y_ * 2 + 0.5) / 7 - 1;
    float4 h = 1 - abs(x) - abs(y);
    float4 b0 = float4(x.xy, y.xy), b1 = float4(x.zw, y.zw);
    float4 s0 = floor(b0) * 2 + 1, s1 = floor(b1) * 2 + 1, sh = -step(h, 0);
    float4 a0 = b0.xzyw + s0.xzyw * sh.xxyy, a1 = b1.xzyw + s1.xzyw * sh.zzww;
    float3 p0 = float3(a0.xy, h.x), p1 = float3(a0.zw, h.y);
    float3 p2 = float3(a1.xy, h.z), p3 = float3(a1.zw, h.w);
    float4 norm = SGTaylorInverseSqrt(float4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3)));
    p0 *= norm.x; p1 *= norm.y; p2 *= norm.z; p3 *= norm.w;
    float4 m = max(0.6 - float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0);
    m *= m;
    return 42 * dot(m * m, float4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
}

float SGNativeDissolveMask(float worldY, float value, float height, float characterY)
{
    // Deliberately uses the native arithmetic and declared height, rather than
    // inventing a new epsilon/shape threshold in a material compatibility path.
    return saturate(2 * value - abs(2 * (worldY - characterY) / height - 1));
}

float3 SGRgbToHsv(float3 color)
{
    float4 K = float4(0, -1.0 / 3.0, 2.0 / 3.0, -1);
    float4 p = lerp(float4(color.bg, K.wz), float4(color.gb, K.xy), step(color.b, color.g));
    float4 q = lerp(float4(p.xyw, color.r), float4(color.r, p.yzx), step(p.x, color.r));
    float d = q.x - min(q.w, q.y);
    // These correspond to the native HSV divide's small literal guard.
    return float3(abs(q.z + (q.w - q.y) / (6 * d + 1e-10)), d / (q.x + 1e-10), q.x);
}

float3 SGHsvToRgb(float3 hsv)
{
    float3 rgb = saturate(abs(frac(hsv.xxx + float3(1, 2.0 / 3.0, 1.0 / 3.0)) * 6 - 3) - 1);
    return hsv.z * lerp(1, rgb, hsv.y);
}

float3 SGNativeEmission(float3 objectPosition, float worldY, float dissolve,
                        float height, float characterY, float noiseScale,
                        float3 emission, float3 glow, float hueScale, float width)
{
    float threshold = SGNativeDissolveMask(worldY, dissolve, height, characterY);
    float noise = SGSimplex(objectPosition * noiseScale) * 0.5 + 0.5;
    clip(noise - threshold);
    float3 hsv = SGRgbToHsv(glow); hsv.x += threshold * hueScale;
    float edge = threshold + (threshold >= 0.1 ? width : 0);
    return lerp(emission, SGHsvToRgb(hsv), step(noise, edge));
}
#endif
