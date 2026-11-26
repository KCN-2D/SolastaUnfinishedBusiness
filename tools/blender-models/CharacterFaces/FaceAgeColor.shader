Shader "Hidden/KCN/FaceAgeColor"
{
    Properties
    {
        _FaceColor ("Source", 2D) = "white" {}
    }

    SubShader
    {
        Pass
        {
            Name "FACE_AGE_COLOR"
            Cull Off
            ZTest Always
            ZWrite Off
            ColorMask RGB

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            sampler2D _FaceColor;
            float4 _FaceColor_ST;
            float4x4 _FaceAnatomy;
            float4 _FaceLandmarks;
            float _FaceAge;
            float _RepairPigment;

            struct FaceVertex
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct FaceTexel
            {
                float4 clip : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 position : TEXCOORD1;
            };

            FaceTexel Vert(FaceVertex input)
            {
                FaceTexel output;
                output.uv = TRANSFORM_TEX(input.uv, _FaceColor);
                output.clip = float4(output.uv * 2 - 1, 0, 1);
                #if UNITY_UV_STARTS_AT_TOP
                output.clip.y = -output.clip.y;
                #endif
                output.position = mul(_FaceAnatomy, input.vertex).xyz;
                return output;
            }

            float Falloff(float distance, float width)
            {
                float ratio = distance / width;
                return exp(-ratio * ratio);
            }

            float Crease(float distance, float width)
            {
                return Falloff(distance, width) - .27 * Falloff(distance - width, 1.1 * width);
            }

            float3 PigmentSpace(float3 color)
            {
                #if !defined(UNITY_COLORSPACE_GAMMA)
                    return LinearToGammaSpace(color);
                #else
                    return color;
                #endif
            }

            void AccumulatePigment(float2 uv, float red, inout float2 sum, inout float2 low, inout float total)
            {
                float3 tap = PigmentSpace(tex2Dlod(_FaceColor, float4(uv, 0, 0)).rgb);
                float weight = 1 - smoothstep(.045, .091, abs(tap.r - red));
                float2 pigment = tap.gb - tap.r;
                sum += pigment * weight;
                total += weight;
                low = min(low, lerp(1, pigment, weight));
            }

            float3 RepairSkinPigment(float3 color, float2 uv, float3 position)
            {
                float side = smoothstep(1.25, 1.95, abs(position.x));
                float cheekAndNape = smoothstep(-5.25, -4.65, position.z) *
                    (1 - smoothstep(-1.45, -.62, position.z));
                float belowEar = smoothstep(_FaceLandmarks.y - 1.95, _FaceLandmarks.y - 1.17, position.y) *
                    (1 - smoothstep(_FaceLandmarks.x - .47, _FaceLandmarks.x - .08, position.y));
                float influence = _RepairPigment * side * cheekAndNape * belowEar;
                if (influence <= 0) return color;

                float3 authored = PigmentSpace(color);
                float2 sum = 0;
                float2 low = 1;
                float total = 0;
                // Compare chroma along the atlas near the cheek and nape. Reject taps
                // across strongly different exposure, such as a dark ear fold.
                [unroll]
                for (int stepIndex = 0; stepIndex < 8; stepIndex++)
                {
                    float2 direction;
                    sincos(stepIndex * .7853981634, direction.y, direction.x);
                    AccumulatePigment(uv + direction * .011, authored.r, sum, low, total);
                    AccumulatePigment(uv + direction * .024, authored.r, sum, low, total);
                    if (stepIndex % 2 == 0)
                        AccumulatePigment(uv + direction * .039, authored.r, sum, low, total);
                }
                float2 reference = lerp(sum / max(total, 1), low, .54);
                float2 delta = authored.gb - authored.r - reference;
                float amount = influence * smoothstep(.033, .089, max(delta.x, delta.y)) * smoothstep(5, 9, total);
                float2 ceiling = authored.r + reference + .014;
                authored.gb = lerp(authored.gb, min(authored.gb, ceiling), amount);
                #if !defined(UNITY_COLORSPACE_GAMMA)
                    authored = GammaToLinearSpace(saturate(authored));
                #endif
                return lerp(color, authored, step(.0001, amount));
            }

            float4 Frag(FaceTexel input) : SV_Target
            {
                float x = input.position.x;
                float y = input.position.y;
                float side = abs(x);
                float asymmetry = .027 * sin(x * 5.4) + .018 * sin(x * 13.1);
                float front = smoothstep(-.74, -.24, input.position.z);
                float forehead = Falloff(side, 1.05) *
                    (.25 * Crease(y - 1.16 + .17 * side * side + asymmetry, .065) +
                     .19 * Crease(y - 1.66 + .22 * side * side + asymmetry, .064) +
                     .12 * Crease(y - 2.06 + .25 * side * side + asymmetry, .061));

                float tear = Falloff(side - .85, .48) *
                    (.27 * Crease(y + .54 + .12 * side + asymmetry, .098) +
                     .12 * Falloff(y + .68 + .17 * side, .26));
                float eyeEdge = smoothstep(1.26, 1.53, side) * Falloff(side - 1.60, .40);
                float eyeFold = eyeEdge *
                    (.24 * Crease(y + .16 + .49 * (side - 1.36) + asymmetry, .062) +
                     .14 * Crease(y + .31 + .64 * (side - 1.36) + asymmetry, .058));

                float smileLevel = saturate((y - _FaceLandmarks.y) / (_FaceLandmarks.x - _FaceLandmarks.y));
                float smileSide = lerp(_FaceLandmarks.w + .13, _FaceLandmarks.z + .15, smileLevel);
                float smile = Falloff(smileLevel - .63, .32) *
                    (.25 * Crease(side - smileSide + asymmetry, .097) +
                     .08 * Falloff(side - smileSide - .08, .24));
                smile *= smoothstep(_FaceLandmarks.y + .04, _FaceLandmarks.y + .30, y) *
                         (1 - smoothstep(_FaceLandmarks.x, _FaceLandmarks.x + .13, y));

                float density = .70 + .30 * sin(x * 4.8 + y * 5.1) * sin(x * 7.4 - y * 4.3);
                float pigment = .068 * density * Falloff(side - 1.2, .58) * Falloff(y - .74 * _FaceLandmarks.x, .71);
                float temple = .07 * Falloff(side - 1.54, .42) * Falloff(y - .60, .51);
                float edge = (1 - smoothstep(1.76, 2.19, side)) * (1 - smoothstep(2.1, 2.71, y));
                float shade = _FaceAge * front * edge * clamp(forehead + tear + eyeFold + smile + pigment + temple, -.09, .36);
                float4 color = tex2D(_FaceColor, input.uv);
                color.rgb = RepairSkinPigment(color.rgb, input.uv, input.position);
                color.rgb *= 1 - shade;
                return color;
            }
            ENDCG
        }
    }
    Fallback Off
}
