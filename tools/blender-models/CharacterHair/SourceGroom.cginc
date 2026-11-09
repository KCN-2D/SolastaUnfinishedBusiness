#ifndef UB_SOURCE_GROOM_COORDINATES_INCLUDED
#define UB_SOURCE_GROOM_COORDINATES_INCLUDED
// Source N/T are the render frame. UV2=(groomU,groomV,roleAndTile,handednessRatio).
// roleAndTile=0 for source/core; integers1..16 select one of the shared tiles.
// UV0.xy retains the native atlas. Format 2 packs support recovery and fiber
// flow into UV0.zw; format 1 reads the authored flow from UV4.xy. UV3 belongs
// to the runtime GPU surface projection and stays free in authored meshes.
float3 SourceGroomSafeNormal(float3 v) { return v*rsqrt(max(dot(v,v),1e-20)); }
float2 SourceGroomUnpackSignedPair(float packed)
{
    float high=floor(packed/4096);
    return float2(packed-high*4096,high)*(2.0/4095)-1;
}
// Projected-area-normalized cylindrical fiber distribution. The fiber axis
// owns the longitudinal lobe; surface GGX alone gives a solid plastic lock.
float SourceGroomFiberDistribution(float tangentHalf, float roughness)
{
    float exponent=max(2/max(roughness*roughness,1e-6)-2,0);
    float z=0.5*(exponent+3);
    float inverseZ=rcp(z);
    // Gamma(z+1/2)/Gamma(z), bounded to <0.02% relative error for z>=1.5.
    float normalization=sqrt(z)*(1+inverseZ*(-0.125+inverseZ*(0.0078125
        +inverseZ*(0.0048828125-inverseZ*0.000640869140625))))/(2*sqrt(UNITY_PI));
    return normalization*pow(max(1-tangentHalf*tangentHalf,0),0.5*exponent);
}
// A direction and its negative describe the same fiber axis. Interpolate
// their symmetric outer product; ordinary vector interpolation cancels them.
float3 SourceGroomInterpolatedAxis(float3 first,float3 second,float3 n,float3 tangent,
                                  float3 bitangent,float3 fallback,out float coherence)
{
    float3 t=SourceGroomSafeNormal(tangent-n*dot(n,tangent));
    float3 b=cross(n,t)*(dot(cross(n,t),bitangent)<0?-1:1);
    float3x3 tensor=float3x3(first.x,first.y,first.z,first.y,second.x,second.y,first.z,second.y,second.z);
    float a=dot(t,mul(tensor,t)),d=dot(b,mul(tensor,b)),c=dot(t,mul(tensor,b));
    float trace=a+d;
    if(trace<=1e-12){coherence=0;return fallback;}
    float split=sqrt(max((a-d)*(a-d)+4*c*c,0));
    float eigenvalue=0.5*(trace+split);
    float2 axis=a>=d ? float2(eigenvalue-d,c) : float2(c,eigenvalue-a);
    float square=dot(axis,axis);
    coherence=saturate(split/trace);
    if(square<=1e-20)return fallback;
    axis*=rsqrt(square);
    return SourceGroomSafeNormal(t*axis.x+b*axis.y);
}

float3 SourceGroomUnpack(float4 p)
{
    float2 xy=float2(p.r*p.a,p.g)*2-1;
    return float3(xy,sqrt(saturate(1-dot(xy,xy))));
}
float3 SourceGroomWhiteout(float3 a,float3 b)
{
    return SourceGroomSafeNormal(float3(a.xy+b.xy,a.z*b.z));
}
float3 SourceGroomNativeIntensity(float3 n,float intensity)
{
    float2 xy=n.xy*intensity;
    return float3(xy,sqrt(saturate(1-dot(xy,xy))));
}
float3 SourceGroomFineInRender(float3 fine,float3 dPdx,float3 dPdy,
                             float2 dUVdx,float2 dUVdy,
                             float3 renderN,float3 renderT,float3 renderB)
{
    float det=dUVdx.x*dUVdy.y-dUVdx.y*dUVdy.x;
    float signDet=det<0?-1:1;
    float3 alongU=(dPdx*dUVdy.y-dPdy*dUVdx.y)*signDet;
    float3 alongV=(-dPdx*dUVdy.x+dPdy*dUVdx.x)*signDet;
    alongU-=renderN*dot(renderN,alongU);
    float lengthSquared=dot(alongU,alongU);
    // A geometrically/UV degenerate projected quad has no defined fine frame.
    // Keep the source normal; authoring and native admission record this case.
    if(lengthSquared<1e-20||abs(det)<1e-20)return float3(0,0,1);
    float3 groomT=alongU*rsqrt(lengthSquared);
    float3 crossB=cross(renderN,groomT);
    float hand=dot(crossB,alongV)<0?-1:1;
    float3 groomB=crossB*hand;
    float3 worldFine=groomT*fine.x+groomB*fine.y+renderN*fine.z;
    // The normal-plane transport analytically retains fine.z. Do not introduce
    // a floating point negative hemisphere, clamp, or all-component reflection.
    return SourceGroomSafeNormal(float3(dot(worldFine,renderT),dot(worldFine,renderB),fine.z));
}
float3 SourceGroomFineWidthInRender(float3 fine,float3 dPdx,float3 dPdy,
                                  float2 dUVdx,float2 dUVdy,
                                  float3 renderN,float3 renderT,float3 renderB)
{
    float det=dUVdx.x*dUVdy.y-dUVdx.y*dUVdy.x;
    float signDet=det<0?-1:1;
    float3 alongU=(dPdx*dUVdy.y-dPdy*dUVdx.y)*signDet;
    float3 alongV=(-dPdx*dUVdy.x+dPdy*dUVdx.x)*signDet;
    float lengthSquared=dot(alongU,alongU);
    if(lengthSquared<1e-20||abs(det)<1e-20)return float3(0,0,1);
    float3 widthU=alongU*rsqrt(lengthSquared);
    // Fine mesh N already contains the authored transverse width tilt.
    // Remove that component before applying the one shader cross-section.
    float3 baseN=renderN-widthU*dot(renderN,widthU);
    float normalLengthSquared=dot(baseN,baseN);
    if(normalLengthSquared<1e-20)return float3(0,0,1);
    baseN*=rsqrt(normalLengthSquared);
    float3 crossB=cross(baseN,widthU);
    float hand=dot(crossB,alongV)<0?-1:1;
    float3 widthB=crossB*hand;
    float3 worldFine=widthU*fine.x+widthB*fine.y+baseN*fine.z;
    // The base changed: all three components belong to the original render TBN.
    return SourceGroomSafeNormal(float3(dot(worldFine,renderT),
        dot(worldFine,renderB),dot(worldFine,renderN)));
}
// Exact native outer surfaces own coverage. Fine tiles change shading only;
// cutting them out can expose the scalp through the inset volume support.
void SourceGroomClip(float sourceAlpha,float sourceCutoff)
{
    clip(sourceAlpha-sourceCutoff);
}
#if defined(UB_NATIVE_SURFACE) && defined(SHADER_API_D3D11)
// This uses the same immutable surfacePoint layout and source-frame equations as
// NativeSurfaceFinishState. Native prefix skinning remains on the CPU.
struct UBSurfacePoint
{
    int index, a, b, c;
    float3 bary, offset, normal, tangent;
    float hand, tangentHand;
};
StructuredBuffer<UBSurfacePoint> _UBSurfacePoints;
StructuredBuffer<float3> _UBSurfacePositions;
StructuredBuffer<float3> _UBSurfaceNormals;
StructuredBuffer<float3> _UBSurfaceTangents;
float4x4 _UBSurfaceSourceInverse, _UBSurfaceSourceTranspose;
float4x4 _UBSurfaceOwnerToObject;
int _UBSurfacePointCount;
float _UBSurfaceProjection;

bool UBSurfaceUnit(inout float3 value)
{
    float lengthValue=length(value);
    if(!isfinite(lengthValue)||lengthValue<=1e-5)return false;
    value/=lengthValue;
    return all(isfinite(value));
}

bool UBGeometryUnit(inout float3 value)
{
    float scale=max(abs(value.x),max(abs(value.y),abs(value.z)));
    if(!isfinite(scale)||scale<=0)return false;
    value/=scale;
    value/=length(value);
    return all(isfinite(value));
}

bool UBEvaluateSurface(uint pointIndex,out float3 position,out float3 normal,out float4 tangent)
{
    position=0;normal=0;tangent=0;
    if(pointIndex>=(uint)_UBSurfacePointCount)return false;
    UBSurfacePoint surfacePoint=_UBSurfacePoints[pointIndex];
    float3 p=_UBSurfacePositions[surfacePoint.a]*surfacePoint.bary.x+
             _UBSurfacePositions[surfacePoint.b]*surfacePoint.bary.y+
             _UBSurfacePositions[surfacePoint.c]*surfacePoint.bary.z;
    float3 n=_UBSurfaceNormals[surfacePoint.a]*surfacePoint.bary.x+
             _UBSurfaceNormals[surfacePoint.b]*surfacePoint.bary.y+
             _UBSurfaceNormals[surfacePoint.c]*surfacePoint.bary.z;
    float3 t=_UBSurfaceTangents[surfacePoint.a]*surfacePoint.bary.x+
             _UBSurfaceTangents[surfacePoint.b]*surfacePoint.bary.y+
             _UBSurfaceTangents[surfacePoint.c]*surfacePoint.bary.z;
    if(surfacePoint.hand<1.5)
    {
        if(!UBSurfaceUnit(n))return false;
        t-=n*dot(n,t);
        if(!UBSurfaceUnit(t))return false;
    }
    float3 b=cross(n,t)*surfacePoint.hand;
    if(surfacePoint.hand>1.5)
    {
        float3 a=_UBSurfacePositions[surfacePoint.a];
        float3 edge=_UBSurfacePositions[surfacePoint.b]-a;
        t=edge;
        n=cross(edge,_UBSurfacePositions[surfacePoint.c]-a);
        if(!UBGeometryUnit(t)||!UBGeometryUnit(n))return false;
        b=cross(n,t);
    }
    p+=t*surfacePoint.offset.x+b*surfacePoint.offset.y+n*surfacePoint.offset.z;
    float3 fn=t*surfacePoint.normal.x+b*surfacePoint.normal.y+n*surfacePoint.normal.z;
    float3 ft=t*surfacePoint.tangent.x+b*surfacePoint.tangent.y+n*surfacePoint.tangent.z;
    if(!UBSurfaceUnit(fn))return false;
    ft-=fn*dot(fn,ft);
    if(!UBSurfaceUnit(ft))return false;
    position=mul(_UBSurfaceSourceInverse,float4(p,1)).xyz;
    normal=mul((float3x3)_UBSurfaceSourceTranspose,fn);
    if(!UBSurfaceUnit(normal))return false;
    ft=mul((float3x3)_UBSurfaceSourceInverse,ft);
    ft-=normal*dot(normal,ft);
    if(!UBSurfaceUnit(ft)||!all(isfinite(position)))return false;
    tangent=float4(ft,surfacePoint.tangentHand);
    return true;
}

void UBApplySurface(float pointIdentifier,inout float4 vertex,inout float3 normal,inout float4 tangent)
{
    if(_UBSurfaceProjection<0.5||pointIdentifier<1)return;
    float3 p,n;float4 t;
    if(!UBEvaluateSurface((uint)pointIdentifier-1,p,n,t))return;
    vertex=float4(mul(_UBSurfaceOwnerToObject,float4(p,1)).xyz,1);
    normal=mul((float3x3)_UBSurfaceOwnerToObject,n);
    t.xyz=mul((float3x3)_UBSurfaceOwnerToObject,t.xyz);
    // The native renderer performs the same single-owner bone transform.
    tangent=t;
}

#endif
#endif
