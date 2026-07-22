// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/DepthWrite"
{
    SubShader
    {
        Pass
        {
            ZWrite On
            ZTest Always
            Cull Off
            ColorMask 0

HLSLPROGRAM
#pragma vertex Vert
#pragma fragment Frag
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

TEXTURE2D_X(_GaussianSplatDepthRT);

struct Attributes { uint vertexID : SV_VertexID; };
struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

Varyings Vert (Attributes input)
{
    Varyings o;
    o.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
    o.uv = GetFullScreenTriangleTexCoord(input.vertexID);
    return o;
}

void Frag (Varyings i, out float outDepth : SV_Depth)
{
    int2 px = int2(i.positionCS.xy);
    float d = LOAD_TEXTURE2D_X(_GaussianSplatDepthRT, px).r;
    // d==0 (clear) = pas de splat opaque ici : ne pas ecrire de depth
    if (d <= 0.0)
        discard;
    outDepth = d;
}
ENDHLSL
        }
    }
}
