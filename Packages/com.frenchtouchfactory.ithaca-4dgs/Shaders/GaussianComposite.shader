// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID&1, (vtxID>>1)&1) * 4.0 - 1.0;
	o.vertex = float4(quadPos, 1, 1);
    return o;
}

Texture2D _GaussianSplatRT;
float _CompositeGamma;   // 1 : melange sRGB (Gaussian4DSplatRenderer.m_SRGBBlending) ; 0 : melange lineaire d'origine

half4 frag (v2f i) : SV_Target
{
    half4 col = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
    // Melange sRGB (comme l'entrainement et le lecteur navigateur) : la couleur premultipliee c*a est rendue
    // telle quelle en sRGB, puis melangee au fond. Avec Blend SrcAlpha, on renvoie donc lin(c*a)/a.
    // Melange lineaire d'origine : lin(c), qui eclaircit les zones translucides (contours).
    if (_CompositeGamma > 0.5)
        return float4(GammaToLinearSpace(col.rgb) / max(col.a, 1e-6), col.a);
    return float4(GammaToLinearSpace(col.rgb/col.a),col.a);
}
ENDCG
        }
    }
}
