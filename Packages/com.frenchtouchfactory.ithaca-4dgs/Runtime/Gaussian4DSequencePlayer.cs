// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace GaussianSplatting4D.Runtime
{
    // Lecture d'un rendu 4DGS ITHACA decoupe en sequences (.ftgsz), avec son, sans asset prepare dans l'editeur.
    //
    // Entree : la description d'un partage ITHACA (JSON de la page de partage : total, fps, sons, decalage_son,
    // sequences {url, premiere, images}) ; url = lien http(s) ou chemin local. Chaque sequence est telechargee (cache
    // temporaire), preparee sur un fil d'arriere-plan (GaussianSplatRuntimeLoader) puis montee dans l'un de deux
    // emplacements : la sequence en cours et la suivante, activee peu avant sa premiere image. Le son mene le temps
    // (horloge de la carte son, comme le lecteur du site) : l'image suit ; si la sequence suivante n'est pas prete a
    // temps, image et son attendent.
    public class Gaussian4DSequencePlayer : MonoBehaviour
    {
        [Serializable]
        public class SequenceInfo
        {
            public string url;
            public string format;
            public int premiere;     // premiere image de la sequence dans le rendu
            public int images;       // nombre d'images
        }

        // cadrage du rendu, regle sur le site : x -> e R (x - pivot) + pivot + position, R = Rz Ry Rx (degres) ; pivot =
        // centre robuste de la premiere sequence (comme la visionneuse du site, cadrage.js)
        [Serializable]
        public class Cadrage
        {
            public float[] position;
            public float[] rotation;
            public float echelle = 1;
            public Boite boite;
        }

        // boite alignee sur les axes de la scene (repere du cadrage, avant retournement) : « interieur » ne garde que ce
        // qui est dedans, « exterieur » que ce qui est dehors, « aucune » ; elle ne suit pas l'echelle du cadrage, comme
        // sur le site, mais suit la taille et la place de ce lecteur
        [Serializable]
        public class Boite
        {
            public string mode;
            public float[] centre;
            public float[] demi;
        }

        [Serializable]
        public class RenduInfo
        {
            public string titre;
            public int total;        // images du rendu
            public float fps = 30;
            public string[] sons;    // pistes son (mp3, wav, m4a), toutes calees sur la premiere image de la prise
            public string[] sons_ogg;    // copies lisibles par Unity sur Android (plateforme ITHACA), preferees si presentes
            public float decalage_son;   // secondes de son avant la premiere image du rendu
            public SequenceInfo[] sequences;
            public Cadrage cadrage;
            [NonSerialized] public Trajectoire trajectoire;
        }

        public enum State { Idle, Loading, Playing, Waiting, Error }

        [Tooltip("Objet de la scene avec Gaussian4DSplatRenderer et Gaussian4DPlayer deja regles (shaders, melange) ; desactive, sert de modele aux deux emplacements")]
        public Gaussian4DSplatRenderer template;
        public GaussianSplatRuntimeLoader.Quality quality = GaussianSplatRuntimeLoader.Quality.VeryHigh;
        public bool loop = true;
        [Tooltip("Sans cadrage : pieds du modele (bas de sa boite robuste) a la hauteur de cet objet. Avec un cadrage, le sol est le plan y = 0 de la scene cadree")]
        public bool feetOnFloor = true;
        [Tooltip("Sans cadrage : centre du modele (horizontalement) sur cet objet. Avec un cadrage, c'est l'origine de la scene cadree")]
        public bool centerOnOrigin = true;
        [Tooltip("Agent des requetes http (celui de Unity est refuse par certains pare-feu de sites)")]
        public string userAgent = "ITHACA-4DGS-Unity/1.0";
        [Tooltip("Garder les sequences telechargees apres la lecture (sinon cache vide a la fermeture du rendu : rien ne reste sur l'appareil)")]
        public bool keepDownloads = false;
        [Tooltip("Rendus ITHACA (repere direct) : retournement sur X pour qu'ils ne soient pas vus en miroir dans Unity")]
        public bool mirrorX = true;
        [Tooltip("Secondes avant sa premiere image ou la sequence suivante est activee (envoi au GPU)")]
        public float activateAhead = 1.0f;

        public State state { get; private set; } = State.Idle;
        public string status { get; private set; } = "";
        public RenduInfo rendu { get; private set; }
        public float currentFrame { get; private set; }
        public float lastPrepareSeconds { get; private set; }
        // demi-largeur horizontale du modele cadre (boite robuste, limitee a la boite du cadrage qui garde l'interieur),
        // en unites de ce lecteur ; 0 tant que la premiere sequence n'est pas prete (ombre au sol, interface)
        public float modelRadius { get; private set; }
        // son : pistes chargees, lecture, ecart entre la position du son et celle de l'image (s)
        public string audioStatus => m_Audio.Count == 0 ? "sans son" :
            $"{m_Audio.Count} piste(s) {(m_Audio.Exists(a => a && a.isPlaying) ? "en lecture" : "arretee(s)")}, " +
            $"ecart son/image {(m_Audio[0] && m_Audio[0].isPlaying ? (m_Audio[0].time - rendu.decalage_son - RenderTime()) * 1000 : 0):0} ms";

        class Slot
        {
            public GameObject go;
            public Gaussian4DSplatRenderer renderer;
            public Gaussian4DPlayer player;
            public GaussianSplatAsset asset;
            public int seq = -1;
            public bool loading;
        }

        readonly Slot[] m_Slots = new Slot[2];
        Transform m_Scene;          // repere de la scene du site (pieds au sol) : cadrage et boite
        Transform m_Cadre;          // cadrage du rendu, parent des emplacements
        GaussianCutout m_Boite;
        bool m_Cadree;
        readonly List<AudioSource> m_Audio = new();
        readonly List<string> m_Downloaded = new();   // fichiers telecharges pendant cette lecture
        static bool s_CachePurged;
        double m_ClockStart;        // horloge (dspTime) correspondant a l'image 0 du rendu
        double m_PausedAt = -1;     // temps du rendu fige pendant une attente
        Coroutine m_Run;

        public void Open(string json)
        {
            json = json.TrimStart('\uFEFF');
            var info = JsonUtility.FromJson<RenduInfo>(json);
            info.trajectoire = Trajectoire.Lire(json);
            Open(info);
        }

        // trajectoire des effets de scene de la plateforme (effets.trajectoire, comme lecteur4d.js) : deplacement rendu
        // au personnage dans le repere du modele, avant le cadrage, interpole entre des images cles ; ex. chat maintenu
        // au niveau du sol. Lue a part : JsonUtility ne lit pas les tableaux de tableaux.
        public class Trajectoire
        {
            public Vector3 axe;
            public float hauteur = 1, fps;
            public List<Vector2> cles = new();

            public static Trajectoire Lire(string json)
            {
                var m = System.Text.RegularExpressions.Regex.Match(json, "\"trajectoire\"\\s*:\\s*\\{(?<corps>.*?\"cles\"\\s*:\\s*\\[(?<cles>.*?)\\]\\s*\\].*?)\\}", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (!m.Success) return null;
                var corps = m.Groups["corps"].Value;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                float Nombre(string cle, float defaut)
                {
                    var n = System.Text.RegularExpressions.Regex.Match(corps, "\"" + cle + "\"\\s*:\\s*(-?[0-9.eE+-]+)");
                    return n.Success ? float.Parse(n.Groups[1].Value, inv) : defaut;
                }
                var a = System.Text.RegularExpressions.Regex.Match(corps, "\"axe\"\\s*:\\s*\\[\\s*(-?[0-9.eE+-]+)\\s*,\\s*(-?[0-9.eE+-]+)\\s*,\\s*(-?[0-9.eE+-]+)\\s*\\]");
                if (!a.Success) return null;
                var t = new Trajectoire
                {
                    axe = new Vector3(float.Parse(a.Groups[1].Value, inv), float.Parse(a.Groups[2].Value, inv), float.Parse(a.Groups[3].Value, inv)).normalized,
                    hauteur = Nombre("hauteur", 1),
                    fps = Nombre("fps", 0),
                };
                foreach (System.Text.RegularExpressions.Match c in System.Text.RegularExpressions.Regex.Matches(m.Groups["cles"].Value + "]", "\\[\\s*(-?[0-9.eE+-]+)\\s*,\\s*(-?[0-9.eE+-]+)\\s*\\]"))
                    t.cles.Add(new Vector2(float.Parse(c.Groups[1].Value, inv), float.Parse(c.Groups[2].Value, inv)));
                t.cles.Sort((x, y) => x.x.CompareTo(y.x));
                return t.cles.Count >= 2 ? t : null;
            }

            // deplacement (repere du modele) a l'instant s (secondes) du rendu
            public Vector3 A(float s, float fpsRendu)
            {
                float f = s * (fps > 0 ? fps : fpsRendu), a = cles[cles.Count - 1].y;
                if (f <= cles[0].x) a = cles[0].y;
                else for (int k = 1; k < cles.Count; k++)
                    if (f <= cles[k].x) { a = cles[k - 1].y + (cles[k].y - cles[k - 1].y) * (f - cles[k - 1].x) / Mathf.Max(1e-6f, cles[k].x - cles[k - 1].x); break; }
                return axe * (a * hauteur);
            }
        }

        public void Open(RenduInfo info)
        {
            Close();
            // cache laisse par une session precedente (application fermee en pleine lecture) : vide au premier rendu
            if (!keepDownloads && !s_CachePurged) { PurgeCache(); s_CachePurged = true; }
            rendu = info;
            m_Run = StartCoroutine(Run());
        }

        public void Close()
        {
            if (m_Run != null) StopCoroutine(m_Run);
            m_Run = null;
            foreach (var a in m_Audio) if (a) { a.Stop(); Destroy(a.clip); Destroy(a.gameObject); }
            m_Audio.Clear();
            for (int i = 0; i < m_Slots.Length; ++i)
                if (m_Slots[i] != null) { FreeSlot(m_Slots[i]); Destroy(m_Slots[i].go); m_Slots[i] = null; }
            if (m_Scene) Destroy(m_Scene.gameObject);
            m_Boite = null;
            modelRadius = 0;
            if (!keepDownloads)
                foreach (var f in m_Downloaded) TryDelete(f);
            m_Downloaded.Clear();
            state = State.Idle;
            status = "";
        }

        void OnDestroy() => Close();

        double Now => AudioSettings.dspTime;
        float Duration => rendu.total / Mathf.Max(rendu.fps, 1);

        IEnumerator Run()
        {
            state = State.Loading;
            if (template == null || rendu == null || rendu.sequences == null || rendu.sequences.Length == 0)
            {
                Fail("rendu ou modele de lecteur absent");
                yield break;
            }
            m_Scene = new GameObject("Scene").transform;
            m_Scene.SetParent(transform, false);
            // repere des rendus ITHACA : direct (comme la visionneuse web, OpenCV) ; Unity est indirect : lues telles
            // quelles, les coordonnees donnent l'image en miroir (main levee du mauvais cote). Retournement sur X : avec
            // celui du changement de repere, une simple rotation ; l'avant du modele reste −z (pose face a l'utilisateur)
            if (mirrorX) m_Scene.localScale = new Vector3(-1, 1, 1);
            m_Cadre = new GameObject("Cadrage").transform;
            m_Cadre.SetParent(m_Scene, false);
            var boite = rendu.cadrage?.boite;
            bool garde = boite != null && (boite.mode == "interieur" || boite.mode == "exterieur");
            if (garde)
            {
                m_Boite = new GameObject("Boite").AddComponent<GaussianCutout>();
                m_Boite.transform.SetParent(m_Scene, false);
                m_Boite.m_Type = GaussianCutout.Type.Box;
                m_Boite.m_Invert = boite.mode == "exterieur";          // decoupe non inversee : garde l'interieur
                m_Boite.transform.localPosition = V3(boite.centre, Vector3.zero);
                m_Boite.transform.localScale = Abs(V3(boite.demi, Vector3.one));     // cube unite [-1, 1] -> demi-cotes
            }
            m_Cadree = false;
            for (int i = 0; i < m_Slots.Length; ++i)
            {
                var go = Instantiate(template.gameObject, m_Cadre, false);
                go.name = $"Sequence{i}";
                go.SetActive(false);
                // place et orientation : celles du lecteur (le modele peut avoir les siennes dans la scene)
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                var pl = go.GetComponent<Gaussian4DPlayer>();
                pl.playOnStart = false;
                pl.loop = false;
                pl.temporalDataAsset = null;
                m_Slots[i] = new Slot { go = go, renderer = go.GetComponent<Gaussian4DSplatRenderer>(), player = pl };
                m_Slots[i].renderer.m_Asset = null;
                m_Slots[i].renderer.m_Cutouts = m_Boite ? new[] { m_Boite } : null;
            }

            // son : toutes les pistes entierement chargees avant la lecture (un decodage au fil de l'eau decale le son)
            var pistes = rendu.sons_ogg != null && rendu.sons_ogg.Length > 0 ? rendu.sons_ogg : rendu.sons;
            if (pistes != null)
                foreach (var url in pistes)
                {
                    status = "son…";
                    using var req = UnityWebRequestMultimedia.GetAudioClip(ToUri(url), AudioTypeOf(url));
                    if (IsRemote(url) && !string.IsNullOrEmpty(userAgent)) req.SetRequestHeader("User-Agent", userAgent);
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success) { Debug.LogWarning($"[Gaussian4DSequencePlayer] son illisible {url} : {req.error}"); continue; }
                    var clip = DownloadHandlerAudioClip.GetContent(req);
                    var src = new GameObject("Son").AddComponent<AudioSource>();
                    src.transform.SetParent(transform, false);
                    src.clip = clip;
                    src.playOnAwake = false;
                    src.spatialBlend = 0;
                    m_Audio.Add(src);
                }

            // premiere sequence avant de demarrer
            yield return Prepare(m_Slots[0], 0);
            if (state == State.Error) yield break;
            Activate(m_Slots[0]);
            StartClock(0);
            state = State.Playing;

            while (true)
            {
                double t = RenderTime();
                if (t >= Duration)
                {
                    if (!loop) { PauseClock(Duration - 1e-4); state = State.Idle; status = "fin"; yield break; }
                    StartClock(t % Duration);
                    t = RenderTime();
                }
                currentFrame = (float)(t * rendu.fps);
                int cur = SequenceAt(currentFrame);
                int next = NextSequence(cur);

                var slotCur = SlotFor(cur);
                if (slotCur == null || slotCur.loading)
                {
                    // sequence pas prete : on attend, image et son figes
                    if (state != State.Waiting) { PauseClock(t); state = State.Waiting; }
                    if (slotCur == null) StartCoroutine(Prepare(FreeSlotExcept(-1), cur));
                    status = $"chargement de la sequence {cur + 1}…";
                    yield return null;
                    continue;
                }
                if (!slotCur.go.activeSelf) Activate(slotCur);
                if (state == State.Waiting) { StartClock(t); state = State.Playing; }

                // emplacement de la suivante : prepare des que possible, active peu avant sa premiere image
                var slotNext = SlotFor(next);
                if (slotNext == null && next != cur)
                {
                    var libre = FreeSlotExcept(cur);
                    if (libre != null) StartCoroutine(Prepare(libre, next));
                }
                else if (slotNext != null && !slotNext.loading && !slotNext.go.activeSelf && next != cur)
                {
                    float avant = (rendu.sequences[next].premiere - currentFrame) / rendu.fps;
                    if (avant < 0) avant += Duration;
                    if (avant <= activateAhead) Activate(slotNext);
                }

                // temps des modeles : temps normalise de la prise (image g a g / (total - 1)), comme le lecteur du site
                float u = currentFrame / Mathf.Max(rendu.total - 1, 1);
                Vector3 marche = rendu.trajectoire != null ? rendu.trajectoire.A((float)t, rendu.fps) : Vector3.zero;
                foreach (var s in m_Slots)
                    if (s.go.activeSelf)
                    {
                        s.go.transform.localPosition = marche;      // trajectoire : avant le cadrage, repere du modele
                        s.player.previewTime = u;
                        // seule la sequence en cours est dessinee ; la suivante attend sur le GPU (avant : dessinee
                        // aussi, en permanence pour un rendu de deux sequences en boucle, deux fois plus de gaussiennes)
                        s.renderer.suspended = s.seq != cur;
                        // une sequence finie est retiree (sauf si c'est aussi la suivante, rendu a une sequence)
                        if (s.seq != cur && s.seq != next) Deactivate(s);
                    }
                status = $"sequence {cur + 1}/{rendu.sequences.Length} · image {(int)currentFrame}/{rendu.total}";
                yield return null;
            }
        }

        int SequenceAt(float frame)
        {
            var seqs = rendu.sequences;
            for (int i = seqs.Length - 1; i >= 0; --i)
                if (frame >= seqs[i].premiere) return i;
            return 0;
        }

        int NextSequence(int i) => i + 1 < rendu.sequences.Length ? i + 1 : (loop ? 0 : i);

        Slot SlotFor(int seq)
        {
            foreach (var s in m_Slots) if (s.seq == seq) return s;
            return null;
        }

        Slot FreeSlotExcept(int keep)
        {
            foreach (var s in m_Slots) if (s.seq < 0 && !s.loading) return s;
            foreach (var s in m_Slots) if (s.seq != keep && !s.loading && !s.go.activeSelf) { FreeSlot(s); return s; }
            return null;
        }

        IEnumerator Prepare(Slot slot, int seq)
        {
            if (slot == null || slot.loading) yield break;
            slot.loading = true;
            slot.seq = seq;
            var info = rendu.sequences[seq];
            string path = info.url;
            if (IsRemote(info.url))
            {
                path = CachePath(info.url);
                if (!File.Exists(path))
                {
                    status = $"telechargement de la sequence {seq + 1}…";
                    using var req = new UnityWebRequest(info.url, UnityWebRequest.kHttpVerbGET) { downloadHandler = new DownloadHandlerFile(path + ".part") { removeFileOnAbort = true } };
                    if (!string.IsNullOrEmpty(userAgent)) req.SetRequestHeader("User-Agent", userAgent);
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success) { Fail($"sequence {seq + 1} : {req.error}"); slot.loading = false; yield break; }
                    File.Move(path + ".part", path);
                    m_Downloaded.Add(path);
                }
            }
            var chrono = System.Diagnostics.Stopwatch.StartNew();
            var task = GaussianSplatRuntimeLoader.PrepareAsync(path, quality);
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) { Fail($"sequence {seq + 1} : {task.Exception?.GetBaseException().Message}"); slot.loading = false; yield break; }
            lastPrepareSeconds = (float)chrono.Elapsed.TotalSeconds;
            slot.asset = GaussianSplatRuntimeLoader.CreateAsset(task.Result);
            if (!m_Cadree) { ApplyCadrage(task.Result.robustMin, task.Result.robustMax); m_Cadree = true; }
            slot.loading = false;
        }

        void Activate(Slot s)
        {
            s.go.SetActive(false);
            s.renderer.m_Asset = s.asset;
            s.player.temporalDataAsset = s.asset.temporalData;   // lu des la premiere activation (Awake)
            s.go.SetActive(true);                                  // envoi au GPU (Gaussian4DSplatRenderer.OnEnable)
            s.player.previewTime = currentFrame / Mathf.Max(rendu.total - 1, 1);
            s.player.SetTemporalData(s.asset.temporalData);
        }

        void Deactivate(Slot s)
        {
            s.go.SetActive(false);
            FreeSlot(s);
        }

        static void FreeSlot(Slot s)
        {
            if (s.go) { s.go.SetActive(false); s.renderer.m_Asset = null; }
            GaussianSplatRuntimeLoader.DestroyAsset(s.asset);
            s.asset = null;
            s.seq = -1;
        }

        static Vector3 V3(float[] a, Vector3 defaut) => a != null && a.Length == 3 ? new Vector3(a[0], a[1], a[2]) : defaut;
        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        void ApplyCadrage(Vector3 lo, Vector3 hi)
        {
            Vector3 pivot = (lo + hi) * 0.5f;
            var c = rendu.cadrage;
            Vector3 pos = V3(c?.position, Vector3.zero), rot = V3(c?.rotation, Vector3.zero);
            float e = c != null && c.echelle > 0 ? c.echelle : 1;
            // R = Rz Ry Rx, comme cadrage.js (et non l'ordre de Quaternion.Euler)
            Quaternion R = Quaternion.AngleAxis(rot.z, Vector3.forward) * Quaternion.AngleAxis(rot.y, Vector3.up) * Quaternion.AngleAxis(rot.x, Vector3.right);
            m_Cadre.localRotation = R;
            m_Cadre.localScale = Vector3.one * e;
            m_Cadre.localPosition = pivot + pos - e * (R * pivot);
            m_Scene.localPosition = Vector3.zero;
            // boite robuste du modele une fois cadre (repere de la scene), limitee a la boite qui garde l'interieur
            Vector3 smin = Vector3.positiveInfinity, smax = Vector3.negativeInfinity;
            for (int i = 0; i < 8; ++i)
            {
                var coin = new Vector3((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z);
                Vector3 p = m_Cadre.localPosition + R * (coin * e);
                smin = Vector3.Min(smin, p); smax = Vector3.Max(smax, p);
            }
            if (m_Boite && !m_Boite.m_Invert)
            {
                Vector3 bc = m_Boite.transform.localPosition, bd = m_Boite.transform.localScale;
                smin = Vector3.Max(smin, bc - bd); smax = Vector3.Min(smax, bc + bd);
            }
            modelRadius = 0.5f * Mathf.Max(smax.x - smin.x, smax.z - smin.z);
            // rendu cadre sur la plateforme : le sol est le plan y = 0 de la scene cadree (le proprietaire y pose les pieds)
            // et l'origine de la scene est le point de pose, comme dans la visionneuse et l'AR du site ; les pieds et le
            // centre ne sont estimes depuis la boite robuste que pour un rendu sans cadrage
            if (c != null) return;
            if (!feetOnFloor && !centerOnOrigin) return;
            // dans le repere du parent de ce lecteur (sans son echelle) : bas de cette boite a la hauteur du lecteur, son
            // centre a l'aplomb du lecteur ; la scene est decalee d'autant
            Quaternion q = transform.localRotation;
            float bas = float.MaxValue;
            for (int i = 0; i < 8; ++i)
            {
                var p = new Vector3((i & 1) != 0 ? smax.x : smin.x, (i & 2) != 0 ? smax.y : smin.y, (i & 4) != 0 ? smax.z : smin.z);
                bas = Mathf.Min(bas, (q * Vector3.Scale(m_Scene.localScale, p)).y);      // retournement sur X compris
            }
            Vector3 centre = q * Vector3.Scale(m_Scene.localScale, (smin + smax) * 0.5f);
            var decalage = new Vector3(centerOnOrigin ? -centre.x : 0, feetOnFloor ? -bas : 0, centerOnOrigin ? -centre.z : 0);
            m_Scene.localPosition = Quaternion.Inverse(q) * decalage;
            Debug.Log($"[Gaussian4DSequencePlayer] cadrage : boite {smin} - {smax}, bas {bas}, centre {centre}, scene {m_Scene.localPosition}");
        }

        void Fail(string message)
        {
            state = State.Error;
            status = message;
            Debug.LogError("[Gaussian4DSequencePlayer] " + message);
        }

        // ---- horloge : carte son (dspTime) ; son decale de decalage_son par rapport a l'image 0 du rendu
        double RenderTime() => m_PausedAt >= 0 ? m_PausedAt : Now - m_ClockStart;

        void StartClock(double renderTime)
        {
            double debut = Now + 0.05;                 // depart commun de toutes les pistes
            m_ClockStart = debut - renderTime;
            m_PausedAt = -1;
            foreach (var a in m_Audio)
            {
                a.Stop();
                double ts = renderTime + rendu.decalage_son;
                if (a.clip == null || ts >= a.clip.length) continue;
                a.time = (float)Math.Max(ts, 0);
                a.PlayScheduled(debut + Math.Max(-ts, 0));
            }
        }

        void PauseClock(double renderTime)
        {
            m_PausedAt = renderTime;
            foreach (var a in m_Audio) a.Stop();
        }

        // ---- fichiers
        static bool IsRemote(string url) => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        static string ToUri(string url) => IsRemote(url) || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? url : new Uri(Path.GetFullPath(url)).AbsoluteUri;

        // cache par chemin sans les parametres (les liens signes changent a chaque lecture)
        static string CacheDir => Path.Combine(Application.temporaryCachePath, "Ithaca4D");

        static void TryDelete(string f)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch (Exception e) { Debug.LogWarning($"[Gaussian4DSequencePlayer] {f} : {e.Message}"); }
        }

        // sequences telechargees et modeles decodes (GaussianSplatRuntimeLoader, FtgszFileReader) des sessions precedentes
        public static void PurgeCache()
        {
            foreach (var d in new[] { CacheDir, Path.Combine(Application.temporaryCachePath, "Ftgsz") })
                if (Directory.Exists(d))
                    foreach (var f in Directory.GetFiles(d)) TryDelete(f);
        }

        static string CachePath(string url)
        {
            string sans = url.Split('?')[0];
            string cle;
            using (var md5 = MD5.Create())
                cle = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(sans))).Replace("-", "").Substring(0, 16);
            string dossier = CacheDir;
            Directory.CreateDirectory(dossier);
            return Path.Combine(dossier, $"{cle}_{Path.GetFileName(sans)}");
        }

        static AudioType AudioTypeOf(string url)
        {
            string ext = Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
            return ext switch
            {
                ".mp3" => AudioType.MPEG,
                ".wav" => AudioType.WAV,
                ".ogg" => AudioType.OGGVORBIS,
                ".m4a" or ".aac" => AudioType.ACC,
                _ => AudioType.UNKNOWN,
            };
        }
    }
}
