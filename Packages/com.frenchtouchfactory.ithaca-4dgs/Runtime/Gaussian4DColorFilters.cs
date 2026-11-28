// SPDX-License-Identifier: MIT
// Copyright (c) 2026 French Touch Factory / ITHACA
using System;
using UnityEngine;

namespace GaussianSplatting4D.Runtime
{
    // Filtres de couleur (28/11/2026), memes calculs que la visionneuse du site ITHACA (lecteur4d/couleurs.js) :
    //
    //  * Colorimetrie : exposition, luminosite, contraste, saturation, temperature, teinte et gamma, appliques a la couleur
    //    de chaque gaussienne (le fond ne change pas). c = couleur sRGB de la gaussienne (0…1) :
    //      1. gain = 2^(exposition / 2,2) x balance (temperature, teinte ; luminance de la balance ramenee a 1) ; c *= gain
    //      2. c = (c - 0,5) x contraste + 0,5 + luminosite
    //      3. c = mix(luminance(c), c, saturation)     (luminance Rec. 709)
    //      4. c = max(c, 0)^(1 / gamma)
    //
    //  * Filtre HSV (« chromakey » du site) : retire les gaussiennes dont la couleur de BASE (degre 0, la meme sous tous les
    //    angles), apres la colorimetrie, a sa teinte, sa saturation et sa valeur dans les trois intervalles (0…1 ; teinte :
    //    bas > haut = intervalle qui passe par le rouge). Avec un cube (Transform, cube unite [-1, 1]), le filtre ne joue
    //    qu'a l'interieur ou qu'a l'exterieur du cube.
    //
    // Script separe : pose sur le meme objet qu'un Gaussian4DSequencePlayer (ou sur un parent de Gaussian4DSplatRenderer),
    // il s'applique a tous les modeles en dessous. Un Gaussian4DSequencePlayer qui ouvre un rendu ITHACA (.4dgs ou
    // catalogue) dont le proprietaire a regle la colorimetrie la reprend ici d'office (ajoute ce script au besoin), sauf si
    // « Use Render Settings » est decoche : les reglages de l'inspecteur sont alors gardes.
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Gaussian Splatting/Gaussian 4D Color Filters")]
    public class Gaussian4DColorFilters : MonoBehaviour
    {
        public enum CubeMode { None, Inside, Outside }

        [Tooltip("Reprendre la colorimetrie et le filtre HSV enregistres avec le rendu ITHACA a son ouverture (Gaussian4DSequencePlayer)")]
        public bool m_UseRenderSettings = true;

        [Header("Color correction")]
        public bool m_ColorCorrection = true;
        [Tooltip("Exposition en IL")]
        [Range(-2f, 2f)] public float m_Exposure = 0f;
        [Range(-0.5f, 0.5f)] public float m_Brightness = 0f;
        [Range(0f, 2f)] public float m_Contrast = 1f;
        [Range(0f, 2f)] public float m_Saturation = 1f;
        [Tooltip("-1 froid … 1 chaud")]
        [Range(-1f, 1f)] public float m_Temperature = 0f;
        [Tooltip("-1 vert … 1 magenta")]
        [Range(-1f, 1f)] public float m_Tint = 0f;
        [Range(0.5f, 2f)] public float m_Gamma = 1f;

        [Header("HSV filter (removes Gaussians of a colour)")]
        public bool m_HsvFilter = false;
        [Tooltip("Intervalle de teinte retire (0…1) ; x > y : intervalle qui passe par le rouge")]
        public Vector2 m_HueRange = new Vector2(0f, 1f);
        [Tooltip("Intervalle de saturation retire (0…1)")]
        public Vector2 m_SaturationRange = new Vector2(0f, 1f);
        [Tooltip("Intervalle de valeur retire (0…1) ; par defaut le noir")]
        public Vector2 m_ValueRange = new Vector2(0f, 0.1f);
        [Tooltip("Limiter le filtre a l'interieur ou a l'exterieur du cube")]
        public CubeMode m_CubeMode = CubeMode.None;
        [Tooltip("Cube du filtre (cube unite [-1, 1] de ce Transform : position = centre, echelle = demi-cotes)")]
        public Transform m_Cube;

        // ── reglages enregistres avec le rendu (index .4dgs, catalogue) : meme JSON que le site ──
        [Serializable]
        public class Colorimetrie
        {
            public float exposition = 0, luminosite = 0, contraste = 1, saturation = 1, temperature = 0, teinte = 0, gamma = 1;
            public Chromakey chromakey;
        }

        [Serializable]
        public class Chromakey
        {
            public bool actif;
            public float[] h, s, v;
            public Gaussian4DSequencePlayer.Boite boite;     // mode « aucune » | « interieur » | « exterieur », centre, demi
        }

        /// <summary>Vrai si ces reglages changent l'image (couleurs non neutres ou filtre HSV actif).</summary>
        public static bool Actif(Colorimetrie c) =>
            c != null && (!Neutre(c.exposition, c.luminosite, c.contraste, c.saturation, c.temperature, c.teinte, c.gamma)
                          || (c.chromakey != null && c.chromakey.actif));

        static bool Neutre(float e, float l, float c, float s, float t, float v, float g) =>
            Mathf.Abs(e) < 1e-6f && Mathf.Abs(l) < 1e-6f && Mathf.Abs(c - 1) < 1e-6f && Mathf.Abs(s - 1) < 1e-6f
            && Mathf.Abs(t) < 1e-6f && Mathf.Abs(v) < 1e-6f && Mathf.Abs(g - 1) < 1e-6f;

        static Vector2 Intervalle(float[] p, Vector2 defaut) =>
            p != null && p.Length == 2 && float.IsFinite(p[0]) && float.IsFinite(p[1])
                ? new Vector2(Mathf.Clamp01(p[0]), Mathf.Clamp01(p[1])) : defaut;

        static Vector3 Trois(float[] p, Vector3 defaut) =>
            p != null && p.Length == 3 && float.IsFinite(p[0]) && float.IsFinite(p[1]) && float.IsFinite(p[2]) ? new Vector3(p[0], p[1], p[2]) : defaut;

        /// <summary>Reprend les reglages d'un rendu (bornes du site). cubeParent : repere de la scene du rendu, ou se
        /// place le cube du filtre (comme la boite du cadrage) ; null = cet objet.</summary>
        public void Appliquer(Colorimetrie c, Transform cubeParent = null)
        {
            c ??= new Colorimetrie();
            m_ColorCorrection = true;
            m_Exposure = Mathf.Clamp(float.IsFinite(c.exposition) ? c.exposition : 0, -2f, 2f);
            m_Brightness = Mathf.Clamp(float.IsFinite(c.luminosite) ? c.luminosite : 0, -0.5f, 0.5f);
            m_Contrast = Mathf.Clamp(float.IsFinite(c.contraste) ? c.contraste : 1, 0f, 2f);
            m_Saturation = Mathf.Clamp(float.IsFinite(c.saturation) ? c.saturation : 1, 0f, 2f);
            m_Temperature = Mathf.Clamp(float.IsFinite(c.temperature) ? c.temperature : 0, -1f, 1f);
            m_Tint = Mathf.Clamp(float.IsFinite(c.teinte) ? c.teinte : 0, -1f, 1f);
            m_Gamma = Mathf.Clamp(float.IsFinite(c.gamma) ? c.gamma : 1, 0.5f, 2f);
            var k = c.chromakey;
            m_HsvFilter = k != null && k.actif;
            m_HueRange = Intervalle(k?.h, new Vector2(0f, 1f));
            m_SaturationRange = Intervalle(k?.s, new Vector2(0f, 1f));
            m_ValueRange = Intervalle(k?.v, new Vector2(0f, 0.1f));
            string mode = k?.boite?.mode;
            m_CubeMode = mode == "interieur" ? CubeMode.Inside : mode == "exterieur" ? CubeMode.Outside : CubeMode.None;
            if (m_CubeMode != CubeMode.None)
            {
                if (!m_Cube)
                {
                    m_Cube = new GameObject("HsvFilterCube").transform;
                }
                m_Cube.SetParent(cubeParent ? cubeParent : transform, false);
                m_Cube.localPosition = Trois(k.boite.centre, Vector3.zero);
                m_Cube.localRotation = Quaternion.identity;
                var d = Trois(k.boite.demi, Vector3.one);
                m_Cube.localScale = new Vector3(Mathf.Max(1e-3f, Mathf.Abs(d.x)), Mathf.Max(1e-3f, Mathf.Abs(d.y)), Mathf.Max(1e-3f, Mathf.Abs(d.z)));
            }
        }

        // ── parametres du shader (Splat4DUtilities.compute, CSCalcViewData) ──
        internal static class Props
        {
            public static readonly int CCOn = Shader.PropertyToID("_CCOn");
            public static readonly int CCGain = Shader.PropertyToID("_CCGain");
            public static readonly int CCParam = Shader.PropertyToID("_CCParam");
            public static readonly int CKOn = Shader.PropertyToID("_CKOn");
            public static readonly int CKBox = Shader.PropertyToID("_CKBox");
            public static readonly int CKH = Shader.PropertyToID("_CKH");
            public static readonly int CKS = Shader.PropertyToID("_CKS");
            public static readonly int CKV = Shader.PropertyToID("_CKV");
            public static readonly int CKMat = Shader.PropertyToID("_CKMat");
        }

        bool CouleursActives => m_ColorCorrection && !Neutre(m_Exposure, m_Brightness, m_Contrast, m_Saturation, m_Temperature, m_Tint, m_Gamma);

        /// <summary>Pousse les parametres des deux filtres pour un modele (repere objet -> monde : rendererMatrix) ;
        /// filters null = aucun filtre (l'etat du compute shader est partage entre les modeles : toujours appele).</summary>
        internal static void Pousser(Gaussian4DColorFilters filters, UnityEngine.Rendering.CommandBuffer cmb, ComputeShader cs, Matrix4x4 rendererMatrix)
        {
            bool cc = filters && filters.isActiveAndEnabled && filters.CouleursActives;
            bool ck = filters && filters.isActiveAndEnabled && filters.m_HsvFilter;
            cmb.SetComputeIntParam(cs, Props.CCOn, cc ? 1 : 0);
            cmb.SetComputeIntParam(cs, Props.CKOn, ck ? 1 : 0);
            if (cc)
            {
                float t = filters.m_Temperature, v = filters.m_Tint;
                var b = new Vector3(1 + 0.2f * t, 1 - 0.2f * v, 1 - 0.2f * t);
                float l = 0.2126f * b.x + 0.7152f * b.y + 0.0722f * b.z, e = Mathf.Pow(2f, filters.m_Exposure / 2.2f);
                cmb.SetComputeVectorParam(cs, Props.CCGain, b * (e / l));
                cmb.SetComputeVectorParam(cs, Props.CCParam, new Vector4(filters.m_Contrast, filters.m_Brightness, filters.m_Saturation, 1f / Mathf.Max(1e-3f, filters.m_Gamma)));
            }
            if (ck)
            {
                Vector2 Borne(Vector2 x) => new Vector2(Mathf.Clamp01(x.x), Mathf.Clamp01(x.y));
                cmb.SetComputeVectorParam(cs, Props.CKH, Borne(filters.m_HueRange));
                cmb.SetComputeVectorParam(cs, Props.CKS, Borne(filters.m_SaturationRange));
                cmb.SetComputeVectorParam(cs, Props.CKV, Borne(filters.m_ValueRange));
                bool cube = filters.m_CubeMode != CubeMode.None && filters.m_Cube;
                cmb.SetComputeIntParam(cs, Props.CKBox, cube ? (filters.m_CubeMode == CubeMode.Inside ? 1 : 2) : 0);
                cmb.SetComputeMatrixParam(cs, Props.CKMat, cube ? filters.m_Cube.worldToLocalMatrix * rendererMatrix : Matrix4x4.identity);
            }
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (!m_HsvFilter || m_CubeMode == CubeMode.None || !m_Cube) return;
            Gizmos.matrix = m_Cube.localToWorldMatrix;
            Gizmos.color = m_CubeMode == CubeMode.Inside ? new Color(0.3f, 0.76f, 1f) : new Color(1f, 0.48f, 0.85f);
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 2f);
        }
#endif
    }
}
