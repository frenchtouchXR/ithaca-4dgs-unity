// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite4D"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            // couleur : melange habituel ; alpha : opacite cumulee (et non a²), pour un fond transparent (AR passthrough du
            // Quest : le compositeur du casque se sert de l'alpha de l'image) ; sans effet sur un fond opaque
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

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

Texture2D _Gaussian4DSplatRT;
float _CompositeGamma;   // 1 : melange sRGB (Gaussian4DSplatRenderer.m_SRGBBlending) ; 0 : melange lineaire d'origine
float _Gaussian4DEyeIndex;   // oeil compose (deux yeux en une passe), -1 sinon
float _Gaussian4DEyeDebug;   // diagnostic : 1 = oeil gauche teinte en rouge, oeil droit en bleu
float _Gaussian4DCompositeFlipY;   // 1 : la cible est l'image finale (orientation d'ecran), lignes de l'image des gaussiennes retournees

float4 frag (v2f i) : SV_Target
{
    // Pleine precision, valeurs invalides ou hors bornes ignorees : sur GPU mobile (Quest 3, Adreno), les gaussiennes
    // presque transparentes donnaient des pixels noirs isoles au bord des silhouettes. Sans effet sur un rendu sain.
    int2 px = int2(i.vertex.xy);
    if (_Gaussian4DCompositeFlipY > 0.5)
    {
        uint w, h;
        _Gaussian4DSplatRT.GetDimensions(w, h);
        px.y = (int)h - 1 - px.y;
    }
    float4 col = _Gaussian4DSplatRT.Load(int3(px, 0));
    if (any(isnan(col)) || any(isinf(col)) || col.a <= 0) discard;
    col = max(col, 0);
    col.a = min(col.a, 1);
    if (_Gaussian4DEyeDebug > 0.5 && _Gaussian4DEyeIndex >= 0)
        col.rgb *= _Gaussian4DEyeIndex < 0.5 ? float3(1, 0.25, 0.25) : float3(0.25, 0.25, 1);
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
