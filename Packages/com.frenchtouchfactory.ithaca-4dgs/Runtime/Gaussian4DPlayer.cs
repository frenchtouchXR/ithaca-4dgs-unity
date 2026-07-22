// SPDX-License-Identifier: MIT
// Gaussian4DPlayer.cs — 4D Gaussian Splatting temporal player for ITHACA

using System;
using System.IO;
using UnityEngine;
using GaussianSplatting4D.Runtime;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GaussianSplatting4D.Runtime
{
    [ExecuteInEditMode]
    [RequireComponent(typeof(Gaussian4DSplatRenderer))]
    public class Gaussian4DPlayer : MonoBehaviour
    {
        [Header("4D Temporal Data")]
        public TextAsset temporalDataAsset;

        [Header("Playback")]
        public float duration = 10.0f;
        public float previewTime = 0f;
        public float fps = 30f;

        [Header("Filtre taille")]
        [Range(0f, 1f)]
        public float maxSplatSize = 0f; // 0 = off
        [Range(0f, 1f)] public float minSplatOpacity = 0f; // 0 = off, coupe les gaussiennes flottantes
        [Range(0f, 1f)] public float temporalCutoff = 0.05f; // seuil marginal_t : coupe les gaussiennes hors fenetre temporelle (0 = off)
        public int numFrames = 145;
        public bool loop = true;
        public bool playOnStart = true;

        bool m_IsPlaying = false;
        float m_CurrentTime = 0f;
        GraphicsBuffer m_GpuTemporalData;

        static readonly int PropSplatTemporal    = Shader.PropertyToID("_SplatTemporal");
        static readonly int PropSplatCurrentTime = Shader.PropertyToID("_SplatCurrentTime");

        Gaussian4DSplatRenderer m_Renderer;

        void Awake() { m_Renderer = GetComponent<Gaussian4DSplatRenderer>(); AutoPopulateTemporalDataAsset(); UploadTemporalData(); }

        void OnEnable()
        {
            if (m_Renderer == null) m_Renderer = GetComponent<Gaussian4DSplatRenderer>();
            if (m_Renderer == null) return;
            // m_GpuTemporalData peut avoir ete dispose par Gaussian4DSplatRenderer.OnDisable()
            // (meme reference partagee via m_Renderer.m_GpuTemporalDummy) sans que notre propre
            // champ soit remis a null -- OnDisable() ci-dessous s'en charge desormais.
            if (m_GpuTemporalData == null && temporalDataAsset != null)
                UploadTemporalData();
#if UNITY_EDITOR
            // Etat de repos en editeur (lancement Unity / ouverture de scene) : toujours
            // repartir de t=0 tant que l'utilisateur n'a pas touche le slider Preview Time
            // lui-meme -- previewTime est un champ serialise qui peut sinon rester bloque sur
            // sa derniere valeur scrubee avant la fermeture de la scene ou avant un Play.
            if (!Application.isPlaying)
                previewTime = 0f;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
            if (m_GpuTemporalData != null)
            {
                m_Renderer.m_GpuTemporalDummy = m_GpuTemporalData;
                m_Renderer.m_Is4D = 1f;
                m_Renderer.m_TemporalCurrentTime = previewTime;
            }
        }

        void OnDisable()
        {
            // Gaussian4DSplatRenderer.OnDisable() -> DisposeResourcesForAsset() dispose deja ce
            // buffer partage (m_Renderer.m_GpuTemporalDummy pointe vers le meme objet). On ne le
            // Release() pas nous-memes ici (double dispose) : on efface juste notre reference pour
            // forcer une vraie recreation au prochain OnEnable, au lieu de reassigner un buffer
            // deja detruit -- c'etait la cause du bug d'affichage au second enable.
            m_GpuTemporalData = null;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
#endif
        }

#if UNITY_EDITOR
        // Sortie de Play : Unity restaure previewTime a sa valeur d'AVANT le Play (comportement
        // natif de serialisation), pas forcement 0 si le slider avait ete deplace auparavant --
        // on force explicitement le retour a t=0 ici, apres coup.
        void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                previewTime = 0f;
                RefreshEditorDisplay();
            }
        }
#endif

        public void RefreshEditorDisplay()
        {
            if (m_Renderer == null) m_Renderer = GetComponent<Gaussian4DSplatRenderer>();
            if (m_Renderer == null) return;
            if (m_GpuTemporalData == null && temporalDataAsset != null)
                UploadTemporalData();
            if (m_GpuTemporalData != null)
            {
                m_Renderer.m_GpuTemporalDummy = m_GpuTemporalData;
                m_Renderer.m_Is4D = 1f;
                m_Renderer.m_TemporalCurrentTime = previewTime;
            }
        }

        void OnValidate()
        {
            previewTime = Mathf.Clamp(previewTime, 0f, duration);
            if (m_Renderer == null) m_Renderer = GetComponent<Gaussian4DSplatRenderer>();
            if (m_Renderer == null) return;
            m_Renderer.m_TemporalCurrentTime = previewTime;
            AutoPopulateTemporalDataAsset();
            if (m_GpuTemporalData == null && temporalDataAsset != null)
                UploadTemporalData();
            if (m_GpuTemporalData != null)
            {
                m_Renderer.m_GpuTemporalDummy = m_GpuTemporalData;
                m_Renderer.m_Is4D = 1f;
            }
        }

        void Start()
        {
            UploadTemporalData();
            if (m_Renderer != null)
            {
                m_Renderer.m_Is4D = 1f;
                SetCurrentTime(previewTime);
            }
            if (playOnStart) Play();
        }

        void OnDestroy() { ReleaseGpuBuffer(); }

        void Update()
        {
            // Garde explicite sur Application.isPlaying, pas seulement m_IsPlaying : avec
            // [ExecuteInEditMode], Start() peut se declencher aussi en mode edition, ce qui
            // appellerait Play() (si playOnStart coche) et mettrait m_IsPlaying a true hors
            // Run -- Update() ne tournant alors qu'au gre des repaints editeur (mouvements de
            // souris, etc.), previewTime avancerait de facon erratique et non voulue.
            if (!Application.isPlaying || !m_IsPlaying)
            {
                SetCurrentTime(previewTime);
                return;
            }
            m_CurrentTime += Time.deltaTime * (duration / (numFrames / fps));
            if (m_CurrentTime >= duration)
            {
                if (loop) m_CurrentTime = m_CurrentTime % duration;
                else { m_CurrentTime = duration; m_IsPlaying = false; }
            }
            previewTime = m_CurrentTime;
            SetCurrentTime(m_CurrentTime);
        }

        public void Play()  { m_IsPlaying = true; }
        public void Stop()  { m_IsPlaying = false; m_CurrentTime = 0f; SetCurrentTime(0f); }
        public void Pause() { m_IsPlaying = false; }

        void SetCurrentTime(float t)
        {
            if (m_GpuTemporalData == null || m_Renderer == null) return;
            m_Renderer.m_GpuTemporalDummy = m_GpuTemporalData;
            m_Renderer.m_TemporalCurrentTime = t;
            m_Renderer.m_MaxSplatSize = maxSplatSize;
            m_Renderer.m_MinSplatOpacity = minSplatOpacity;
            m_Renderer.m_TemporalCutoff = temporalCutoff;
            m_Renderer.m_Is4D = 1f;
        }

#if UNITY_EDITOR
        // Auto-assigne temporalDataAsset si vide, en cherchant <nomAsset>_tmp.bytes a cote de
        // l'asset assigne dans Gaussian4DSplatRenderer.m_Asset (meme dossier, meme nom de base
        // + suffixe _tmp -- convention utilisee par GaussianSplatAssetCreator, voir doc section
        // 4.2/7.2). Editeur seulement : AssetDatabase indisponible en build.
        void AutoPopulateTemporalDataAsset()
        {
            if (temporalDataAsset != null) return;
            if (m_Renderer == null) m_Renderer = GetComponent<Gaussian4DSplatRenderer>();
            if (m_Renderer == null || m_Renderer.m_Asset == null) return;

            string assetPath = AssetDatabase.GetAssetPath(m_Renderer.m_Asset);
            if (string.IsNullOrEmpty(assetPath)) return;

            string dir = Path.GetDirectoryName(assetPath);
            string baseName = Path.GetFileNameWithoutExtension(assetPath);
            string candidatePath = Path.Combine(dir, baseName + "_tmp.bytes").Replace("\\", "/");

            var found = AssetDatabase.LoadAssetAtPath<TextAsset>(candidatePath);
            if (found != null)
            {
                temporalDataAsset = found;
                Debug.Log($"[Gaussian4DPlayer] Temporal Data Asset auto-assigne : {candidatePath}");
                EditorUtility.SetDirty(this);
            }
        }
#endif

        void UploadTemporalData()
        {
            if (temporalDataAsset == null) { Debug.LogWarning("[Gaussian4DPlayer] No temporal data asset."); return; }
            ReleaseGpuBuffer();
            byte[] bytes = temporalDataAsset.bytes;
            m_GpuTemporalData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, bytes.Length / 4, 4) { name = "Gaussian4DTemporalData" };
            uint[] uintData = new uint[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, uintData, 0, bytes.Length);
            m_GpuTemporalData.SetData(uintData);
            int nSplats = bytes.Length / 52;
            Debug.Log($"[Gaussian4DPlayer] {nSplats} splats");
            m_Renderer.m_GpuTemporalDummy = m_GpuTemporalData;
        }

        void ReleaseGpuBuffer() { m_GpuTemporalData?.Release(); m_GpuTemporalData = null; }
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(Gaussian4DPlayer))]
    public class Gaussian4DPlayerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Dessine tous les champs par defaut SAUF previewTime (slider gere manuellement,
            // borne dynamiquement sur duration plutot que sur un [Range] fige a 10).
            SerializedProperty prop = serializedObject.GetIterator();
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (prop.name == "previewTime") continue;
                EditorGUILayout.PropertyField(prop, true);
            }

            var p = (Gaussian4DPlayer)target;
            SerializedProperty previewProp = serializedObject.FindProperty("previewTime");
            EditorGUI.BeginChangeCheck();
            float newPreview = EditorGUILayout.Slider("Preview Time", previewProp.floatValue, 0f, Mathf.Max(p.duration, 0.01f));
            if (EditorGUI.EndChangeCheck())
                previewProp.floatValue = newPreview;

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("── Playback ──", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(!Application.isPlaying);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("▶ Play"))  p.Play();
            if (GUILayout.Button("⏸ Pause")) p.Pause();
            if (GUILayout.Button("⏹ Stop"))  p.Stop();
            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();
        }
    }
#endif
}
