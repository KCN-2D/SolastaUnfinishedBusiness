Shader "KCN/SourceGroom/NativeHair"
{
    Properties
    {
        [HideInInspector] _UBSurfaceProjection ("Native surface projection", Float) = 0
        [HideInInspector] _SupportInVertexFrame ("Vertex support layout", Float) = 0
        [HideInInspector] _FlowInVertexFrame ("Vertex fiber flow", Float) = 0
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("MainTex", 2D) = "white" {}
        _Cutoff ("Mask Clip Value", Float) = 0.5
        _Smoothness ("Smoothness", Range(0,1)) = 0.2
        [Normal] _BumpMap ("BumpMap", 2D) = "bump" {}
        _Mask ("Mask", 2D) = "black" {}
        _Aniso ("Aniso", 2D) = "white" {}
        _Normal_Intensity ("Normal_Intensity", Range(0,1)) = 0.5
        _Spec_Color ("Spec_Color", Color) = (0,0,0,0)
        _EmissionColor ("EmissionColor", Color) = (0,0,0,0)
        _HP_Normal_Intensity ("HP_Normal_Intensity", Range(0,3)) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _CullMode ("CullMode", Float) = 2
        _DisolveValue ("DisolveValue", Range(0,1)) = 0
        _DisolveNoiseScale ("DisolveNoiseScale", Float) = 1
        _CharacterHeight ("CharacterHeight", Float) = 0
        _DisolveGlowColor ("DisolveGlowColor", Color) = (0,0,0,0)
        _DisolveColorWidth ("DisolveColorWidth", Float) = 0
        _DisolveHueScale ("DisolveHueScale", Float) = 0
        _DisolveVertexOffset ("DisolveVertexOffset", Float) = 0
        _CharacterPosY ("CharacterPosY", Float) = 0
        [HideInInspector] _texcoord ("", 2D) = "white" {}
        [HideInInspector] __dirty ("", Float) = 1
        _GroomColor ("Groom variation and cutout", 2D) = "white" {}
        [Normal] _GroomNormal ("Groom normal", 2D) = "bump" {}
        _FiberSmoothness ("Fiber smoothness", Range(0,0.95)) = 0.55
        _FiberAnisotropy ("Fiber anisotropy", Range(0,0.95)) = 0.8
        _MacroNormalStrength ("Native lock normal strength", Range(0,1)) = 0.35
        _BakedColorDetail ("Native baked contrast", Range(0,1)) = 0.7
        _BakedColorMipBias ("Native pigment filter", Range(0,5)) = 0.5
        _NativePigmentMean ("Native pigment reference", Float) = 0
        _FiberColorStrength ("Fiber pigment variation", Range(0,0.5)) = 0.35
        _FiberAlphaCutoff ("Fiber alpha cutoff", Range(0, 1)) = 0
        _CoreResponse ("Continuous source response", Range(0,1)) = 1
        _CoreAnisotropyScale ("Core fiber direction", Range(0,1)) = 0.75
        _CoreSmoothnessScale ("Core fiber smoothness", Range(0.5,1)) = 0.9
        _CoreFineStrength ("Core fine strand shading", Range(0,1)) = 1
        _NativeCoreCull ("Respect native source core culling", Range(0,1)) = 0
        _FineAlphaCutoff ("Fine32 measured coverage cutoff", Range(0,1)) = 0.35
        _CoreReliefStrength ("Core fine normal strength", Range(0,1)) = 0.12
        _FineReliefStrength ("Fine32 normal strength", Range(0,1)) = 0.35
        _CoreGroomPigmentStrength ("Core groom pigment contrast", Range(0,1)) = 0.1
        _AiryVisibility ("Authored fine visibility", Float) = 1
        _AiryDensity ("Authored fine hierarchy density", Range(0,1)) = 1
    }

    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" "IsEmissive"="true" "UBNativeSurface"="True" "UBGeometrySurface"="True" }
        Cull Off
        CGPROGRAM
        #pragma target 4.5
        #pragma only_renderers d3d11
        #define UB_NATIVE_SURFACE 1
        #pragma surface surf SourceGroom fullforwardshadows addshadow vertex:vert exclude_path:deferred keepalpha
        #include "SourceGroomProgram.cginc"
        ENDCG
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" "IsEmissive"="true" }
        Cull Off
        CGPROGRAM
        #pragma target 3.5
        #pragma surface surf SourceGroom fullforwardshadows addshadow vertex:vert exclude_path:deferred keepalpha
        #include "SourceGroomProgram.cginc"
        ENDCG
    }
    Fallback Off
}
