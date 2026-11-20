        #include "UnityCG.cginc"
        #include "UnityPBSLighting.cginc"
        #include "SourceGroom.cginc"
        #include "NativeFamilyFx.cginc"

        sampler2D _MainTex, _BumpMap, _Mask, _Aniso, _GroomColor, _GroomNormal;
        float4 _MainTex_ST, _BumpMap_ST, _Mask_ST, _texcoord_ST;
        float4 _Color, _Spec_Color, _EmissionColor, _DisolveGlowColor;
        float _Cutoff, _Smoothness, _HP_Normal_Intensity, _Normal_Intensity;
        float _DisolveValue, _DisolveNoiseScale, _CharacterHeight, _CharacterPosY;
        float _DisolveColorWidth, _DisolveHueScale, _DisolveVertexOffset;
        float _FiberSmoothness, _FiberAnisotropy, _MacroNormalStrength;
        float _BakedColorDetail, _BakedColorMipBias, _FiberColorStrength;
        float _FiberAlphaCutoff, _CoreFineStrength, _CoreResponse, _AiryVisibility, _AiryDensity;
        float _CoreAnisotropyScale, _CoreSmoothnessScale;
        float _FineAlphaCutoff, _CoreReliefStrength, _FineReliefStrength, _CoreGroomPigmentStrength, _NativeGrooveStrength;
        float _NativeCoreCull, _CullMode, _NativePigmentMean;
        float _SupportInVertexFrame, _FlowInVertexFrame;
        #ifdef SOURCE_GROOM_ROLE_PROOF
        float _RoleDebug, _RoleOnly;
        #endif

        struct SurfaceOutputSourceGroom
        {
            fixed3 Albedo;
            float3 Normal;
            half3 Emission;
            half Metallic;
            half Smoothness;
            half Occlusion;
            fixed Alpha;
            float3 FiberDirection;
            half FiberAnisotropy;
            half FiberVariation;
        };




        SurfaceOutputStandard SourceGroomStandard(SurfaceOutputSourceGroom s)
        {
            SurfaceOutputStandard o;
            o.Albedo=s.Albedo; o.Normal=s.Normal; o.Emission=s.Emission;
            o.Metallic=s.Metallic; o.Smoothness=s.Smoothness;
            o.Occlusion=s.Occlusion; o.Alpha=s.Alpha;
            return o;
        }

        half4 LightingSourceGroom(SurfaceOutputSourceGroom s,float3 viewDir,UnityGI gi)
        {
            // Emission is added by the generated forward surface pass. No F0,
            // ambient term or extra light may contaminate the role signal.
            #ifdef SOURCE_GROOM_ROLE_PROOF
            if(_RoleDebug>0.5)
            {
                #if defined(UNITY_PASS_FORWARDADD)
                    return half4(0,0,0,0);
                #else
                    return half4(0,0,0,1);
                #endif
            }
            #endif
            SurfaceOutputStandard surface=SourceGroomStandard(s);
            // Metallic clips and ornaments keep the native Standard response.
            if(s.Metallic>0.5)return LightingStandard(surface,viewDir,gi);


            surface.Normal=normalize(surface.Normal);
            float3 n=surface.Normal;
            float3 tangent=s.FiberDirection-n*dot(n,s.FiberDirection);
            if(dot(tangent,tangent)<1e-8)
                tangent=cross(n,abs(n.z)<0.99?float3(0,0,1):float3(0,1,0));
            float3 t=SourceGroomSafeNormal(tangent);
            float3 l=gi.light.dir;
            float3 h=Unity_SafeNormalize(l+viewDir);
            float nl=saturate(dot(n,l)),nv=max(abs(dot(n,viewDir)),1e-4);
            float nh=saturate(dot(n,h)),lh=saturate(dot(l,h));
            float perceptualRoughness=1-s.Smoothness;
            float roughness=max(perceptualRoughness*perceptualRoughness,0.002);
            // Every nonmetal hair surface represents fibers. Flow coherence
            // broadens their longitudinal distribution; it must not mix a
            // solid-surface GGX lobe back into the opaque lock underneath them.
            float fiberRoughness=lerp(perceptualRoughness,roughness,saturate(s.FiberAnisotropy));
            // Small changes in fiber tilt stagger the highlight along a lock.
            // The filtered strand signal keeps the shift stable at a distance.
            float fiberHalfAngle=dot(t,h)+0.11*s.FiberVariation*s.FiberAnisotropy;
            float fiberDistribution=SourceGroomFiberDistribution(fiberHalfAngle,fiberRoughness);
            float internalDistribution=SourceGroomFiberDistribution(
                abs(fiberHalfAngle)-0.11,min(fiberRoughness+0.23,1));
            float fiberMask=min(1,min(2*nh*nv/max(lh,1e-5),2*nh*nl/max(lh,1e-5)));
            float fiberVisibility=fiberMask/(4*max(nv*nl,1e-5));
            float specularTerm=max(fiberVisibility*UNITY_PI*nl,0);
            #if defined(_SPECULARHIGHLIGHTS_OFF)
                specularTerm=0;
            #endif
            half oneMinusReflectivity;
            half3 specular;
            half3 diffuse=DiffuseAndSpecularFromMetallic(s.Albedo,s.Metallic,specular,oneMinusReflectivity);
            // Native gloss colours remain selectable within the dielectric
            // range. Their reflected light is evaluated by this BRDF once.
            specular=lerp(specular,0.03+0.02*saturate(_Spec_Color.rgb),_CoreResponse);
            diffuse=EnergyConservationBetweenDiffuseAndSpecular(s.Albedo,specular,oneMinusReflectivity);
            UnityGI indirect=gi;indirect.light.color=0;
            // Only the core-owned Standard/environment reflection is isolated.
            // The ambient diffuse and independent dielectric F0 remain intact.


            SurfaceOutputStandardSpecular environmentSurface;
            environmentSurface.Albedo=surface.Albedo;environmentSurface.Normal=surface.Normal;
            environmentSurface.Emission=surface.Emission;environmentSurface.Specular=specular;
            environmentSurface.Smoothness=surface.Smoothness;environmentSurface.Occlusion=surface.Occlusion;
            environmentSurface.Alpha=surface.Alpha;
            half4 color=LightingStandardSpecular(environmentSurface,viewDir,indirect);
            half diffuseTerm=DisneyDiffuse(nv,nl,lh,perceptualRoughness)*nl;
            // Use a broader, pigment-filtered reflection for light scattered
            // within the fibers. Absolute angle keeps the axis sign independent of UV seams.
            float internalWeight=0.24*_CoreResponse*s.FiberAnisotropy;
            half3 fiberReflection=lerp(fiberDistribution,
                internalDistribution*sqrt(saturate(s.Albedo)),internalWeight);
            color.rgb+=gi.light.color*(diffuse*diffuseTerm
                +FresnelTerm(specular,lh)*specularTerm*fiberReflection);
            return color;
        }

        void LightingSourceGroom_GI(SurfaceOutputSourceGroom s,UnityGIInput data,inout UnityGI gi)
        {
            #ifdef SOURCE_GROOM_ROLE_PROOF
            if(_RoleDebug>0.5){gi=(UnityGI)0;return;}
            #endif
            if(s.Metallic>0.5){LightingStandard_GI(SourceGroomStandard(s),data,gi);return;}
            float3 n=normalize(s.Normal);
            float3 t=SourceGroomSafeNormal(s.FiberDirection-n*dot(n,s.FiberDirection));
            float3 radialView=data.worldViewDir-t*dot(t,data.worldViewDir);
            float square=dot(radialView,radialView);
            float3 fiberNormal=square>1e-8 ? radialView*rsqrt(square) : n;
            float3 reflectionNormal=SourceGroomSafeNormal(lerp(n,fiberNormal,_CoreResponse*s.FiberAnisotropy));
            float3 f0=lerp(unity_ColorSpaceDielectricSpec.rgb,0.03+0.02*saturate(_Spec_Color.rgb),_CoreResponse);
            // Keep diffuse GI on the actual surface. Probe reflection follows
            // the same fiber axis as direct light, with one existing probe lookup.
            Unity_GlossyEnvironmentData glossy=UnityGlossyEnvironmentSetup(s.Smoothness,
                data.worldViewDir,reflectionNormal,f0);
            gi=UnityGlobalIllumination(data,s.Occlusion,n,glossy);
        }



        struct SourceGroomVertex
        {
            float4 vertex : POSITION;
            float4 tangent : TANGENT;
            float3 normal : NORMAL;
            float4 texcoord : TEXCOORD0;
            float4 texcoord1 : TEXCOORD1;
            float4 texcoord2 : TEXCOORD2;
            float4 texcoord3 : TEXCOORD3;
            float4 texcoord4 : TEXCOORD4;
            fixed4 color : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Input
        {
            float sourceFacing : VFACE;
            float4 sourceAndGroom;
            float4 groomRoleAndSign;
            float3 worldFiberFirst;
            float3 worldFiberSecond;
            float3 worldPos;
            float3 worldNormal;
            INTERNAL_DATA
        };

        void vert(inout SourceGroomVertex v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            #if defined(UB_NATIVE_SURFACE) && defined(SHADER_API_D3D11)
            UBApplySurface(v.texcoord3.x, v.vertex, v.normal, v.tangent);
            #endif
            float worldY = mul(unity_ObjectToWorld, v.vertex).y;
            float dissolve = SGNativeDissolveMask(worldY, _DisolveValue, _CharacterHeight, _CharacterPosY);
            v.vertex.xyz += v.normal * (_DisolveVertexOffset * dissolve);
            o.sourceAndGroom.xy = v.texcoord.xy * _texcoord_ST.xy + _texcoord_ST.zw;
            o.sourceAndGroom.zw = v.texcoord2.xy;
            o.groomRoleAndSign.xy = v.texcoord2.zw;
            o.groomRoleAndSign.zw = _FlowInVertexFrame>1.5 ? SourceGroomUnpackSignedPair(v.texcoord.z) : v.texcoord.zw;
            float3 frameN=SourceGroomSafeNormal(v.normal);
            float3 frameT=SourceGroomSafeNormal(v.tangent.xyz-frameN*dot(frameN,v.tangent.xyz));
            float3 frameB=cross(frameN,frameT)*v.tangent.w;
            float2 vertexFlow=_FlowInVertexFrame>1.5 ? SourceGroomUnpackSignedPair(v.texcoord.w) : v.texcoord4.xy;
            float3 flow=UnityObjectToWorldDir(frameT*vertexFlow.x+frameB*vertexFlow.y);
            o.worldFiberFirst=float3(flow.x*flow.x,flow.x*flow.y,flow.x*flow.z);
            o.worldFiberSecond=float3(flow.y*flow.y,flow.y*flow.z,flow.z*flow.z);
        }





        void surf(Input i, inout SurfaceOutputSourceGroom o)
        {
            float2 cardUV=i.sourceAndGroom.zw;
            float2 cardDx=ddx(cardUV),cardDy=ddy(cardUV);
            float3 dPdx=ddx(i.worldPos),dPdy=ddy(i.worldPos);
            float2 sourceSampleUV=i.sourceAndGroom.xy*_MainTex_ST.xy+_MainTex_ST.zw;
            float2 sourceDx=ddx(sourceSampleUV),sourceDy=ddy(sourceSampleUV);
            float2 normalUV=i.sourceAndGroom.xy*_BumpMap_ST.xy+_BumpMap_ST.zw;
            float2 nativeDx=ddx(normalUV),nativeDy=ddy(normalUV);
            float2 maskUV=i.sourceAndGroom.xy*_Mask_ST.xy+_Mask_ST.zw;
            float2 maskDx=ddx(maskUV),maskDy=ddy(maskUV);
            float role=i.groomRoleAndSign.x;
            bool sourceRoot=role>=96.5;
            float4 source=0;
            float mask=0;
            if(role<64.5 || sourceRoot)
            {
                source=tex2Dgrad(_MainTex,sourceSampleUV,sourceDx,sourceDy);
                mask=tex2Dgrad(_Mask,maskUV,maskDx,maskDy).r;
            }
            o.Emission=SGNativeEmission(mul(unity_WorldToObject,float4(i.worldPos,1)).xyz,i.worldPos.y,_DisolveValue,
                _CharacterHeight,_CharacterPosY,_DisolveNoiseScale,_EmissionColor.rgb,
                _DisolveGlowColor.rgb,_DisolveHueScale,_DisolveColorWidth);
            if(role>=64.5 && mask<=0.5)
            {
                bool undercoat=role>=80.5;
                float3 strand=SourceGroomStrandSample(cardUV,role,cardDx,cardDy);
                if(sourceRoot)SourceGroomClip(source.a,_Cutoff);
                if(!undercoat)
                {
                    clip(_AiryVisibility-0.5);
                    clip(_AiryDensity-(abs(i.groomRoleAndSign.y)-1));
                    clip(strand.y-0.24);
                }
                float3 renderN=normalize(WorldNormalVector(i,float3(0,0,1)));
                float3 renderT=normalize(WorldNormalVector(i,float3(1,0,0)));
                float3 renderB=normalize(WorldNormalVector(i,float3(0,1,0)));
                float coherence;
                o.FiberDirection=SourceGroomInterpolatedAxis(i.worldFiberFirst,i.worldFiberSecond,
                    renderN,renderT,renderB,renderB,coherence);
                float pigment=_NativePigmentMean>0 ? clamp(0.12+0.82*_NativePigmentMean,0.29,0.66) : 0.45;
                float root=smoothstep(0,0.59,cardUV.y);
                o.Albedo=_Color.rgb*pigment*lerp(undercoat ? 0.65 : 0.76,1,root)*(1+0.41*strand.x);
                o.Normal=SourceGroomSafeNormal(float3((undercoat ? 0.12 : 0.26)*strand.z,0.045*strand.x,1));
                if(sourceRoot)
                {
                    float3 native=SourceGroomUnpack(tex2Dgrad(_BumpMap,normalUV,nativeDx,nativeDy));
                    native.y*=i.groomRoleAndSign.y<0 ? -1 : 1;
                    float localValue=dot(source.rgb,float3(0.2126,0.7152,0.0722));
                    o.Albedo=_Color.rgb*lerp(pigment,clamp(localValue,0.20,0.67),0.24)*(0.94+0.52*strand.x);
                    o.Normal=SourceGroomWhiteout(SourceGroomNativeIntensity(native,0.34),o.Normal);
                }
                o.Metallic=0;
                o.Smoothness=(sourceRoot ? lerp(0.28,0.34,root) : lerp(0.31,0.40,root))+0.07*strand.x;
                o.Occlusion=lerp(0.75,1,root);
                o.Alpha=1;
                o.FiberAnisotropy=coherence*_FiberAnisotropy*(undercoat ? 0.62 : 1);
                o.FiberVariation=0.25*strand.x;
                #ifdef SOURCE_GROOM_ROLE_PROOF
                o.Emission+=0.1*_RoleDebug;
                #endif
                return;
            }
            if(sourceRoot)role=0; // The native metallic surface keeps its original material.
            // Gradients are evaluated before clip and outside the role branch. The
            // branch therefore never asks a divergent quad for derivatives.
            float2 dUVdx=cardDx,dUVdy=cardDy;
            // Optical classification only. Source role IDs, alpha/clip,
            // geometry, native core culling and fine17..48 stay unchanged.

            if(_NativeCoreCull>0.5 && role<0.5)
            {
                // Unity CullMode:0 Off,1 Front,2 Back. Do not alter the source
                // material value or apply it to newly owned fine surfaces.
                if(_CullMode>0.5 && _CullMode<1.5)clip(-i.sourceFacing);
                else if(_CullMode>=1.5)clip(i.sourceFacing);
            }
            // Roles: 0=source, 1..16=legacy tufts, 17..48=individual fibers,
            // 49..64=layered cards using the atlas's full strand alpha/normal.
            // Local XY remain independent, physically scaled coordinates.
            bool airy=role>=16.5 && role<48.5;
            bool edgeFiber=airy && role>=32.5;
            bool atlasCard=role>=48.5;
            // Only new roles carry hierarchy rank in abs(W)-1; its sign is retained.
            if(airy || atlasCard){clip(_AiryVisibility-.5);clip(_AiryDensity-(abs(i.groomRoleAndSign.y)-1));}
            float tile=fmod(max(0,floor(role)-1),16);
            float2 origin=float2(fmod(tile,4),floor(tile/4))*0.25;
            float2 groomSampleScale=float2(0.92,0.95)*0.25;
            // Differentiate the continuous coordinates before tile-local Repeat.
            float2 groomCoordinates=atlasCard?saturate(i.sourceAndGroom.zw):frac(i.sourceAndGroom.zw);
            float2 groomSampleUV=origin+float2(0.04,0.025)*0.25+groomCoordinates*groomSampleScale;
            float3 renderN=normalize(WorldNormalVector(i,float3(0,0,1)));
            float3 renderT=normalize(WorldNormalVector(i,float3(1,0,0)));
            float3 renderB=normalize(WorldNormalVector(i,float3(0,1,0)));
            float groomDet=dUVdx.x*dUVdy.y-dUVdx.y*dUVdy.x;
            bool fineCoordinates=role>=0.5 || (_CoreFineStrength>0 && abs(groomDet)>1e-20);
            float fineWeight=role>=0.5?1:_CoreFineStrength;
            float strandVariation=0;
            float strandOcclusion=1;
            float4 groom = float4(1,1,1,1);
            float3 fine = float3(0,0,1);
            if (fineCoordinates && mask <= 0.5)
            {
                groom = tex2Dgrad(_GroomColor, groomSampleUV, dUVdx*groomSampleScale, dUVdy*groomSampleScale);
                // The opaque support represents strand bundles. Its normal
                // bands stay resolvable at portrait size; separate transparent
                // fibers retain the atlas's individual-strand coordinates.
                float2 normalScale=role<0.5 ? float2(0.45,0.45) : float2(1,1);
                float2 normalCoordinates=atlasCard?groomCoordinates:frac(i.sourceAndGroom.zw*normalScale);
                float2 normalUV=origin+float2(0.04,0.025)*0.25+normalCoordinates*groomSampleScale;
                float2 normalDx=dUVdx*normalScale,normalDy=dUVdy*normalScale;
                fine = SourceGroomUnpack(tex2Dgrad(_GroomNormal, normalUV, normalDx*groomSampleScale, normalDy*groomSampleScale));
                fine = lerp(float3(0,0,1),SourceGroomFineInRender(fine,dPdx,dPdy,normalDx,normalDy,renderN,renderT,renderB),fineWeight);
                float reliefStrength=(airy || atlasCard) ? _FineReliefStrength : (role<0.5 ? _CoreReliefStrength : 1);
                if(reliefStrength<1 && !airy)fine=SourceGroomNativeIntensity(fine,reliefStrength);
                if(airy)
                {
                    // One resolvable width cross-section; no microscopic cycle
                    // or optical-model selection is shipped with fine32.
                    float visibleHalfWidth=sqrt(max(1-_FineAlphaCutoff,0.001));
                    float transverse=clamp((2*i.sourceAndGroom.zw.x-1)/visibleHalfWidth,-1,1)*_FineReliefStrength;
                    float3 crossSection=float3(transverse,0,sqrt(saturate(1-transverse*transverse)));
                    fine=SourceGroomFineWidthInRender(crossSection,dPdx,dPdy,dUVdx,dUVdy,renderN,renderT,renderB);
                }


            }
            SourceGroomClip(source.a, _Cutoff);
            if(role >= 0.5 && !airy && !atlasCard && mask <= 0.5 && _FiberAlphaCutoff > 0)
            {
                clip(groom.a - _FiberAlphaCutoff);
            }
            // Atlas alpha represents fractional strand coverage. Use its
            // midpoint at each mip so thin ends retain their authored width.
            if(atlasCard && mask<=0.5)clip(groom.a-0.5);
            // New fine32 sheets own measured alpha; native core and legacy scalp
            // coverage retain their original alpha rule and separate cutoff.
            if(airy && mask<=0.5)
            {
                float fineCoverage=edgeFiber ? saturate(4*i.sourceAndGroom.zw.x*(1-i.sourceAndGroom.zw.x)) : groom.a;
                if(!edgeFiber)
                {
                    float endCoverage=smoothstep(0,0.08,i.sourceAndGroom.zw.y)*smoothstep(0,0.08,1-i.sourceAndGroom.zw.y);
                    fineCoverage=saturate(4*i.sourceAndGroom.zw.x*(1-i.sourceAndGroom.zw.x))*endCoverage;
                }
                clip(fineCoverage-_FineAlphaCutoff);
            }
            float proofRole=mask>0.5 ? 4 : (role<0.5 ? 0 : (edgeFiber ? 3 : (airy ? 2 : 1)));
            #ifdef SOURCE_GROOM_ROLE_PROOF
            if(_RoleOnly>=0)clip(0.25-abs(proofRole-_RoleOnly));
            #endif
            float3 macro = SourceGroomUnpack(tex2Dgrad(_BumpMap,normalUV,nativeDx,nativeDy));
            macro.y *= i.groomRoleAndSign.y < 0 ? -1 : 1;
            // Native locks describe broad volume; their deep grooves must not
            // amplify the independent fine-fiber relief. Ornaments keep native N.
            float3 shadingMacro = (airy || atlasCard) && mask<=0.5 ? float3(0,0,1) : (mask <= 0.5 ? SourceGroomNativeIntensity(macro, lerp(_MacroNormalStrength,_MacroNormalStrength*0.5,_CoreResponse)) : macro);
            if(!airy && !atlasCard && mask<=0.5 && _CoreResponse>0)
            {
                // Retain the source's strand grooves separately from its broad
                // sculpted volumes. Scaling the complete native normal down
                // erased those grooves as well, especially on sparse grooms.
                float3 broad=SourceGroomUnpack(tex2Dgrad(_BumpMap,normalUV,nativeDx*4,nativeDy*4));
                float3 grooves=SourceGroomSafeNormal(float3((macro.xy-broad.xy)*(_CoreResponse*_NativeGrooveStrength),1));
                shadingMacro=SourceGroomWhiteout(SourceGroomNativeIntensity(broad,
                    lerp(_MacroNormalStrength,_MacroNormalStrength*0.5,_CoreResponse)),grooves);
            }
            float3 combined = fineCoordinates && mask <= 0.5 ? SourceGroomWhiteout(shadingMacro,fine) : shadingMacro;
            float3 normal = SourceGroomNativeIntensity(combined, _HP_Normal_Intensity);
            // Support normals use the standard vertex interpolator. The inverse
            // carry restores the original frame for metallic source pixels.
            // Older catalogs retain their original tangent-space carry layout.
            bool applyCarry=_SupportInVertexFrame>0.5 ? mask>0.5 : mask<=0.5;
            if(applyCarry && dot(i.groomRoleAndSign.zw,i.groomRoleAndSign.zw)>1e-12)
            {
                float2 xy=i.groomRoleAndSign.zw;
                float3 support=float3(xy,sqrt(saturate(1-dot(xy,xy))));
                float3 axis=float3(-support.y,support.x,0);
                normal=normalize(normal+cross(axis,normal)+cross(axis,cross(axis,normal))/max(1+support.z,1e-4));
            }
            // The modern lookup follows the smooth support. Sculpted normal
            // detail belongs to the fiber BRDF, rather than a second albedo lobe.
            float3 nativeNormal = SourceGroomNativeIntensity(macro, _HP_Normal_Intensity);
            float3 view = normalize(_WorldSpaceCameraPos.xyz - i.worldPos);
            float3 lookupNormal=lerp(nativeNormal,float3(0,0,1),_CoreResponse);
            float3 specNormal = normalize(WorldNormalVector(i, lerp(float3(0,0,1), lookupNormal, _Normal_Intensity)));
            float3 lookup = tex2D(_Aniso, reflect(-view, specNormal).xy).rgb;
            float nativeNv=saturate(dot(normalize(WorldNormalVector(i,lookupNormal)),view));
            float nativeFresnel=0.033736+0.966264*pow(1-nativeNv,5);
            float3 nativeLookupPigment=lookup*_Spec_Color.rgb*(1-nativeFresnel);
            float3 pigment=source.rgb;
            if(mask<=0.5)
            {
                // Native diffuse atlases contain sculpted highlight/shadow pairs.
                // Reduce that baked relief at the sampler's actual mip level;
                // its lower-frequency pigment and the native dye still own hue.
                float filterScale=exp2(_BakedColorMipBias+1.5*_CoreResponse);
                float3 filtered=tex2Dgrad(_MainTex,sourceSampleUV,
                    sourceDx*filterScale,sourceDy*filterScale).rgb;
                float luminance=dot(pigment,float3(0.2126,0.7152,0.0722));
                float filteredLuminance=dot(filtered,float3(0.2126,0.7152,0.0722));
                float localLuminance=filteredLuminance;
                // De-light the broad painted volumes while retaining the
                // native strand contrast above that local spatial frequency.
                // The two samples use the actual projected mip footprint.
                if(_NativePigmentMean>0)filteredLuminance=_NativePigmentMean;
                float legacyLuminance=lerp(filteredLuminance,luminance,_BakedColorDetail);
                float nativeStrandRatio=clamp(luminance/max(localLuminance,1e-4),0.25,4);
                strandOcclusion=1-0.22*_CoreResponse*saturate(1-nativeStrandRatio);
                float strandLuminance=filteredLuminance*lerp(1,nativeStrandRatio,_BakedColorDetail);
                float modernWeight=_NativePigmentMean>0 ? _CoreResponse : 0;
                float gain=lerp(legacyLuminance,strandLuminance,modernWeight)/max(luminance,1e-4);

                // Filter brightness only: adjacent coloured atlas regions must
                // not change this strand's native pigment chromaticity.
                gain=min(gain,1/max(max(pigment.r,max(pigment.g,pigment.b)),1e-4));
                pigment*=gain*_Color.rgb;
                if(fineCoordinates)
                {
                    float variation=SourceGroomStrandSample(groomCoordinates,tile,dUVdx,dUVdy).x;
                    strandVariation=variation;
                    strandOcclusion*=1-0.19*_CoreResponse*fineWeight*saturate(-strandVariation);
                    float3 groomPigment=groom.rgb;
                    // Core alpha is intentionally intact: transparent groom gaps
                    // must not stamp full-strength dark pigment onto that core.
                    if(role<0.5 && _CoreGroomPigmentStrength<1)
                        groomPigment=lerp(1,groomPigment,_CoreGroomPigmentStrength);
                    pigment*=groomPigment*(1+_FiberColorStrength*fineWeight*variation);
                }
            }
            // The legacy fake reflection is already represented by the modern
            // fiber BRDF. Adding it to pigment would wash out dark dyes and
            // turn pale hair into opaque sculpted highlights.
            o.Albedo = pigment+(mask<=0.5 ? nativeLookupPigment*(1-_CoreResponse) : 0);
            o.Normal = normal;


            o.Metallic = mask;
            // The same source-flow field changes the microscopic luster in both
            // forward passes. It is filtered before minification and never
            // changes native pigment alpha, geometry, native dye or metal masks.
            float lusterScale=role<0.5 ? _CoreSmoothnessScale : 1;
            float strandLuster=saturate(_FiberSmoothness*lusterScale+0.12*strandVariation
                +0.035*(groom.a-0.5));
            o.Smoothness = mask <= 0.5 ? lerp(_FiberSmoothness,strandLuster,_CoreResponse) : _Smoothness;
            o.Occlusion = strandOcclusion;
            o.Alpha = 1;
            float det=dUVdx.x*dUVdy.y-dUVdx.y*dUVdy.x;
            float3 alongV=(-dPdx*dUVdy.x+dPdy*dUVdx.x)*(det<0?-1:1);
            alongV-=renderN*dot(renderN,alongV);
            o.FiberDirection=fineCoordinates&&abs(det)>1e-20&&dot(alongV,alongV)>1e-20
                ? SourceGroomSafeNormal(alongV) : renderB;
            float axisCoherence=1;
            if(_FlowInVertexFrame>0.5)
                o.FiberDirection=SourceGroomInterpolatedAxis(i.worldFiberFirst,i.worldFiberSecond,
                    renderN,renderT,renderB,o.FiberDirection,axisCoherence);
            // The measured groom coordinates supply the fiber direction on both
            // the opaque support and the individual sheets.
            o.FiberAnisotropy=axisCoherence*_FiberAnisotropy*(role<0.5 ? (fineCoordinates ? _CoreAnisotropyScale : 0) : 1);
            o.FiberVariation=strandVariation*_CoreResponse;

            #ifdef SOURCE_GROOM_ROLE_PROOF
            if(_RoleDebug>0.5)
            {
                float3 color=proofRole<0.5 ? float3(0,0,1) : (proofRole<1.5 ? float3(1,0,0) :
                    (proofRole<2.5 ? float3(0,1,0) : (proofRole<3.5 ? float3(1,0,1) : float3(1,1,0))));
                o.Albedo=0;o.Metallic=0;o.Smoothness=0;o.Occlusion=1;o.Emission=color;
                // A custom surface lighting model also evaluates surf in each
                // ForwardAdd pass. The role is emitted once by ForwardBase.
                #if defined(UNITY_PASS_FORWARDADD)
                    o.Emission=0;
                #endif
            }


            #endif
        }
