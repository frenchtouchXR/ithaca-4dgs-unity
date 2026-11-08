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
        }

        [Serializable]
        public class RenduInfo
        {
            public string titre;
            public int total;        // images du rendu
            public float fps = 30;
            public string[] sons;    // pistes son (mp3, wav, m4a), toutes calees sur la premiere image de la prise
            public float decalage_son;   // secondes de son avant la premiere image du rendu
            public SequenceInfo[] sequences;
            public Cadrage cadrage;
        }

        public enum State { Idle, Loading, Playing, Waiting, Error }

        [Tooltip("Objet de la scene avec Gaussian4DSplatRenderer et Gaussian4DPlayer deja regles (shaders, melange) ; desactive, sert de modele aux deux emplacements")]
        public Gaussian4DSplatRenderer template;
        public GaussianSplatRuntimeLoader.Quality quality = GaussianSplatRuntimeLoader.Quality.VeryHigh;
        public bool loop = true;
        [Tooltip("Pieds du modele (bas de sa boite robuste, une fois cadre) a la hauteur de cet objet")]
        public bool feetOnFloor = true;
        [Tooltip("Agent des requetes http (celui de Unity est refuse par certains pare-feu de sites)")]
        public string userAgent = "ITHACA-4DGS-Unity/1.0";
        [Tooltip("Garder les sequences telechargees apres la lecture (sinon cache vide a la fermeture du rendu : rien ne reste sur l'appareil)")]
        public bool keepDownloads = false;
        [Tooltip("Secondes avant sa premiere image ou la sequence suivante est activee (envoi au GPU)")]
        public float activateAhead = 1.0f;

        public State state { get; private set; } = State.Idle;
        public string status { get; private set; } = "";
        public RenduInfo rendu { get; private set; }
        public float currentFrame { get; private set; }
        public float lastPrepareSeconds { get; private set; }
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
        Transform m_Cadre;          // cadrage du rendu (et pieds au sol), parent des emplacements
        bool m_Cadree;
        readonly List<AudioSource> m_Audio = new();
        readonly List<string> m_Downloaded = new();   // fichiers telecharges pendant cette lecture
        static bool s_CachePurged;
        double m_ClockStart;        // horloge (dspTime) correspondant a l'image 0 du rendu
        double m_PausedAt = -1;     // temps du rendu fige pendant une attente
        Coroutine m_Run;

        public void Open(string json) => Open(JsonUtility.FromJson<RenduInfo>(json));

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
            if (m_Cadre) Destroy(m_Cadre.gameObject);
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
            m_Cadre = new GameObject("Cadrage").transform;
            m_Cadre.SetParent(transform, false);
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
            }

            // son : toutes les pistes entierement chargees avant la lecture (un decodage au fil de l'eau decale le son)
            if (rendu.sons != null)
                foreach (var url in rendu.sons)
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
                foreach (var s in m_Slots)
                    if (s.go.activeSelf)
                    {
                        s.player.previewTime = u;
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

        void ApplyCadrage(Vector3 lo, Vector3 hi)
        {
            Vector3 pivot = (lo + hi) * 0.5f;
            var c = rendu.cadrage;
            Vector3 V(float[] a) => a != null && a.Length == 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;
            Vector3 pos = V(c?.position), rot = V(c?.rotation) * Mathf.Deg2Rad;
            float e = c != null && c.echelle > 0 ? c.echelle : 1;
            // R = Rz Ry Rx, comme cadrage.js (et non l'ordre de Quaternion.Euler)
            Matrix4x4 R = Matrix4x4.Rotate(Quaternion.AngleAxis(rot.z * Mathf.Rad2Deg, Vector3.forward))
                        * Matrix4x4.Rotate(Quaternion.AngleAxis(rot.y * Mathf.Rad2Deg, Vector3.up))
                        * Matrix4x4.Rotate(Quaternion.AngleAxis(rot.x * Mathf.Rad2Deg, Vector3.right));
            m_Cadre.localRotation = R.rotation;
            m_Cadre.localScale = Vector3.one * e;
            m_Cadre.localPosition = pivot + pos - e * R.MultiplyVector(pivot);
            if (!feetOnFloor) return;
            // bas de la boite robuste cadree, dans le repere du parent de ce lecteur : decale pour etre a sa hauteur
            float bas = float.MaxValue;
            for (int i = 0; i < 8; ++i)
            {
                var coin = new Vector3((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z);
                Vector3 local = m_Cadre.localPosition + m_Cadre.localRotation * (coin * e);   // repere de ce lecteur
                bas = Mathf.Min(bas, (transform.localRotation * Vector3.Scale(local, transform.localScale)).y);
            }
            m_Cadre.localPosition += Quaternion.Inverse(transform.localRotation) * new Vector3(0, -bas, 0) / Mathf.Max(transform.localScale.y, 1e-6f);
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
