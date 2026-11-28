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
    // Lecture d'un rendu 4DGS ITHACA decoupe en sequences (.ftgsz, .trgz), avec son, sans asset prepare dans l'editeur.
    //
    // Entree : la description d'un partage ITHACA (JSON de la page de partage : total, fps, sons, decalage_son,
    // sequences {url, premiere, images}) ou un fichier .4dgs ; url = lien http(s) ou chemin local.
    //
    // Lecture fluide (24/11/2026, portage du lecteur JS 0.4.0) :
    //   - une fenetre de sequences en avance reste chargee (budget memoire de l'appareil, memoryBudgetMB), preparees par
    //     plusieurs fils d'arriere-plan (decoders) : decodage en memoire, recalage temporel et fenetres aussi hors du
    //     fil principal ;
    //   - envoi au GPU par morceaux dans un budget par image (≈ uploadMsPerFrame ms, regle sur le cout mesure), la plus
    //     proche d'abord (GaussianSplatGpuData) ; une sequence necessaire tout de suite et pas encore envoyee est finie
    //     d'un coup en dernier recours (stats.forcedUploads, doit rester 0) ; le reste (activation du rendu) une etape
    //     par image ;
    //   - fondu enchaine (rendus avec transition) : modele de jonction des deux sequences trie d'un bloc
    //     (GaussianSplatJunction, prepare en arriere-plan), poids continus sur tout le recouvrement a densite constante
    //     (α' = 1 − (1 − α)^w) ; jonction gardee jusqu'au bout de son recouvrement ;
    //   - horloge a vitesse strictement constante (25/11/2026), ancree sur le son ; le temps ne recule jamais.
    //
    // Portage de la visionneuse du site 20261127h (28/11/2026) :
    //   - personnage jamais efface : quand la sequence suivante n'est pas prete, la derniere image dessinee reste a
    //     l'ecran (meme instant, memes poids), le temps et le son attendent (aussi au bouclage) ; reprise des que la
    //     suivante est prete (stats.heldFrames) ;
    //   - pas de saut du temps apres un gel (entree / sortie du mode immersif ou de l'AR, application en arriere-plan) :
    //     plus de ~0,75 s sans image, le son est coupe et repart de l'image affichee (stats.clockResumes) ;
    //   - budget memoire tenu a tout instant : capacite recalculee a chaque sequence sur son poids reel (GPU : donnees
    //     et tampons du rendu ; processeur : donnees preparees gardees pendant l'envoi), place d'une sequence en vol
    //     laissee ; un decodage ne part que si sa place tient dans le budget ; mobile (Quest), sequences lourdes
    //     (> 150 Mo) : 2 decodages et 1 envoi en attente au plus (stats.memoryMB) ;
    //   - fin de boucle : les premieres sequences restent en memoire pendant toute la lecture (2 a partir de 6 places, 1
    //     a partir de 4 ; stats.keptAtStart) ;
    //   - depart selon le debit reel de preparation : sequences pretes par seconde comparees au debit de lecture ;
    //     preparation moins de 1,25 fois plus rapide → depart fenetre pleine ; vitesse de lecture jamais changee
    //     (stats.prepRate, stats.playRate). Avant le depart, la premiere image est deja dessinee (state Waiting).
    //   - tri : sur le GPU a chaque image (Gaussian4DSplatRenderer) ; evite quand l'instant n'a pas change et que la
    //     camera a peu bouge (m_SkipStillSorts : pause, image tenue).
    public class Gaussian4DSequencePlayer : MonoBehaviour
    {
        [Serializable]
        public class SequenceInfo
        {
            public string url;
            public string format;
            public int premiere;     // premiere image de la sequence dans le rendu
            public int images;       // nombre d'images
            public long offset;      // .4dgs : plage d'octets de la sequence dans le fichier url (octets > 0)
            public long octets;
            // transition entre sequences (12/11/2026, rendus avec recouvrement) : images sur lesquelles la sequence a ete
            // entrainee, cœur compris ; trainImages = 0 : sans transition (coupure nette, comme avant)
            public int trainPremiere;
            public int trainImages;
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
            public Fichier fichier;      // rendu en un seul fichier .4dgs (sans liste de sequences : ouvert par son index)
            [NonSerialized] public Trajectoire trajectoire;
            [NonSerialized] public List<SonPlage> sonsPlage;   // .4dgs : pistes son, plages d'octets du fichier
        }

        [Serializable]
        public class Fichier
        {
            public string url;           // lien http(s) ou chemin local
            public string format;        // « 4dgs »
        }

        public class SonPlage
        {
            public string url, type;
            public long offset, octets;
        }

        public enum State { Idle, Loading, Playing, Waiting, Error }

        // mesures de la lecture (comme stats du lecteur JS)
        [Serializable]
        public class Stats
        {
            public int forcedUploads;        // sequences finies d'un coup (envoi pas termine a temps) : doit rester 0
            public float uploadMB;           // envoye au GPU depuis l'ouverture
            public float uploadMs;           // duree des envois de la derniere image
            public float uploadBudgetMB;     // budget d'envoi par image en cours
            public int hardSyncs;            // recalages durs de l'horloge sur le son (ecart > 0,25 s)
            public int softSyncFrames;       // toujours 0 : plus de rattrapage doux (vitesse constante, 25/11/2026)
            public int junctions;            // modeles de jonction prepares (fondu enchaine)
            public int capacity;             // sequences gardees chargees
            public int decoders;             // preparations en parallele
            public float sequenceMB;         // memoire d'une sequence (GPU)
            public int waits;                // attentes d'une sequence pas prete
            public float activateMs;         // derniere activation d'un modele envoye (adoption, tampons de tri)
            public float releaseMs;          // derniere liberation d'un modele
            public double clockRealtime;     // instant reel (Time.realtimeSinceStartupAsDouble) de currentFrame
            // 28/11/2026 (portage de la visionneuse du site 20261127h)
            public int heldFrames;           // images tenues : derniere image gardee a l'ecran, sequence suivante pas prete
            public int clockResumes;         // gels (> 0,75 s sans image) : son coupe, reprise a l'image affichee
            public float memoryMB;           // memoire estimee (GPU + processeur) de tout ce que le lecteur tient
            public float budgetMB;           // budget memoire de l'appareil
            public int keptAtStart;          // premieres sequences gardees en memoire (fin de boucle sans rechargement)
            public float prepRate;           // depart : sequences pretes par seconde
            public float playRate;           // depart : sequences lues par seconde
            public int sortsSkipped;         // tris evites (camera immobile, instant inchange) depuis l'ouverture
        }

        [Tooltip("Objet de la scene avec Gaussian4DSplatRenderer et Gaussian4DPlayer deja regles (shaders, melange) ; desactive, sert de modele aux sequences chargees")]
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
        [Tooltip("Secondes d'avance minimales : l'envoi au GPU d'une sequence est accelere s'il risque de ne pas etre fini ce temps avant sa premiere image")]
        public float activateAhead = 0.6f;
        [Tooltip("Rendus avec transition (recouvrement des sequences) : fondu enchaine entre deux sequences voisines, comme la visionneuse du site ; sinon coupure nette")]
        public bool crossfade = true;
        [Tooltip("Memoire (Mo) des sequences gardees chargees en avance ; 0 = automatique (selon l'appareil)")]
        public float memoryBudgetMB = 0;
        [Tooltip("Sequences preparees en parallele (fils d'arriere-plan) ; 0 = automatique")]
        public int decoders = 0;
        [Tooltip("Duree visee des envois au GPU par image (ms) ; le budget en octets est regle sur le cout mesure")]
        public float uploadMsPerFrame = 4f;
        [Tooltip("Ne pas decompresser les couleurs (harmoniques) au-dela du degre dessine par le modele (m_SHOrder) : memes images, preparation plus rapide")]
        public bool skipUnusedSH = true;

        public State state { get; private set; } = State.Idle;
        public string status { get; private set; } = "";
        public RenduInfo rendu { get; private set; }
        public float currentFrame { get; private set; }
        public float lastPrepareSeconds { get; private set; }
        public Stats stats { get; private set; } = new Stats();
        public bool paused => m_Paused;
        // demi-largeur horizontale du modele cadre (boite robuste, limitee a la boite du cadrage qui garde l'interieur),
        // en unites de ce lecteur ; 0 tant que la premiere sequence n'est pas prete (ombre au sol, interface)
        public float modelRadius { get; private set; }
        // son : pistes chargees, lecture, ecart entre la position du son et celle de l'image (s)
        public string audioStatus => m_Audio.Count == 0 ? "sans son" :
            $"{m_Audio.Count} piste(s) {(m_Audio.Exists(a => a && a.isPlaying) ? "en lecture" : "arretee(s)")}, " +
            $"ecart son/image {(m_AudioOn ? (Heard() - m_Time) * 1000 : 0):0} ms";

        public void Open(string json)
        {
            json = json.TrimStart('\uFEFF');
            var info = JsonUtility.FromJson<RenduInfo>(json);
            info.trajectoire = Trajectoire.Lire(json);
            if ((info.sequences == null || info.sequences.Length == 0) && !string.IsNullOrEmpty(info.fichier?.url))
            {
                Open4dgs(info.fichier.url, info.titre);
                return;
            }
            Open(info);
        }

        // Video en un seul fichier .4dgs (lien http(s) ou chemin local) : index lu a la fin du fichier, puis sequences et
        // son lus par plages d'octets au fil de la lecture (format : Utils/Format4dgs.cs)
        public void Open4dgs(string url, string titre = null)
        {
            Close();
            state = State.Loading;
            status = "ouverture du fichier .4dgs…";
            m_Ouverture = StartCoroutine(Ouvrir4dgs(url, titre));
        }

        Coroutine m_Ouverture;

        IEnumerator Ouvrir4dgs(string url, string titre)
        {
            string json = null;
            if (!IsRemote(url))
            {
                try { json = Utils.Format4dgs.LireIndexLocal(url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(url).LocalPath : url); }
                catch (Exception e) { m_Ouverture = null; Fail(e.Message); yield break; }
            }
            else
            {
                // fin du fichier (Range suffixe) ; index plus long que la fin lue : demande a part
                byte[] fin = null; long taille = 0;
                using (var req = UnityWebRequest.Get(url))
                {
                    req.SetRequestHeader("Range", $"bytes=-{Utils.Format4dgs.Queue}");
                    if (!string.IsNullOrEmpty(userAgent)) req.SetRequestHeader("User-Agent", userAgent);
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success) { m_Ouverture = null; Fail($".4dgs : {req.error}"); yield break; }
                    fin = req.downloadHandler.data;
                    var cr = req.GetResponseHeader("Content-Range");
                    taille = req.responseCode == 206 && cr != null && cr.Contains("/") ? long.Parse(cr.Substring(cr.LastIndexOf('/') + 1)) : fin.Length;
                }
                if (!Utils.Format4dgs.LireFin(fin, taille, out long pos, out int lg)) { m_Ouverture = null; Fail(".4dgs illisible (fin du fichier)"); yield break; }
                long base_ = taille - fin.Length;
                if (pos >= base_) json = Encoding.UTF8.GetString(fin, (int)(pos - base_), lg);
                else
                    using (var req = UnityWebRequest.Get(url))
                    {
                        req.SetRequestHeader("Range", $"bytes={pos}-{pos + lg - 1}");
                        if (!string.IsNullOrEmpty(userAgent)) req.SetRequestHeader("User-Agent", userAgent);
                        yield return req.SendWebRequest();
                        if (req.result != UnityWebRequest.Result.Success) { m_Ouverture = null; Fail($".4dgs : {req.error}"); yield break; }
                        json = Encoding.UTF8.GetString(req.downloadHandler.data, 0, lg);
                    }
            }
            Utils.Format4dgs.Index idx;
            try { idx = Utils.Format4dgs.Analyser(json); }
            catch (Exception e) { m_Ouverture = null; Fail(e.Message); yield break; }
            var info = new RenduInfo
            {
                titre = string.IsNullOrEmpty(idx.titre) ? titre : idx.titre,
                total = idx.images, fps = idx.fps > 0 ? idx.fps : 30, decalage_son = idx.decalage_son,
                cadrage = idx.cadrage,
                trajectoire = Trajectoire.Lire(json),
                sequences = Array.ConvertAll(idx.sequences, b => new SequenceInfo { url = url, format = b.encodage == "trgz1" ? "trgz" : "ftgsz", premiere = b.premiere, images = b.images, offset = b.offset, octets = b.octets,
                                                                                  trainPremiere = b.entrainement_images > 0 ? b.entrainement_premiere : b.premiere, trainImages = b.entrainement_images }),
                sonsPlage = new List<SonPlage>(),
            };
            // une piste par nom ; parmi ses copies (m4a, Ogg…), celle que Unity lit partout (Ogg d'abord)
            var parNom = new Dictionary<string, Utils.Format4dgs.Bloc>();
            foreach (var b in idx.sons ?? Array.Empty<Utils.Format4dgs.Bloc>())
                if (!parNom.TryGetValue(b.nom ?? "", out var deja) || Utils.Format4dgs.RangSon(b.type) < Utils.Format4dgs.RangSon(deja.type)) parNom[b.nom ?? ""] = b;
            foreach (var b in parNom.Values) info.sonsPlage.Add(new SonPlage { url = url, type = b.type, offset = b.offset, octets = b.octets });
            m_Ouverture = null;
            Open(info);
        }

        // Plage d'octets [offset, offset + octets) d'un fichier (lien ou chemin) copiee dans chemin (cache)
        IEnumerator Extraire(string url, long offset, long octets, string chemin, Action<string> erreur)
        {
            if (!IsRemote(url))
            {
                try { Utils.Format4dgs.ExtraireLocal(url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(url).LocalPath : url, offset, octets, chemin); }
                catch (Exception e) { erreur(e.Message); }
                yield break;
            }
            using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET) { downloadHandler = new DownloadHandlerFile(chemin + ".part") { removeFileOnAbort = true } };
            req.SetRequestHeader("Range", $"bytes={offset}-{offset + octets - 1}");
            if (!string.IsNullOrEmpty(userAgent)) req.SetRequestHeader("User-Agent", userAgent);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) { erreur(req.error); yield break; }
            if (req.responseCode != 206) { TryDelete(chemin + ".part"); erreur("le serveur ne lit pas les plages d'octets (Range)"); yield break; }
            if (File.Exists(chemin)) File.Delete(chemin);
            File.Move(chemin + ".part", chemin);
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

        // ---- modeles charges : une entree par sequence (cle = indice) et par jonction (cle = kJonction + a)
        const int kJonction = 1 << 20;
        enum Etape { Chargement, Envoi, Pret }

        class Entree
        {
            public int cle, a = -1;              // a : jonction des sequences a et a + 1
            public bool jonction => a >= 0;
            public Etape etape;
            public int gen;
            public bool telechargement;
            public GaussianSplatRuntimeLoader.Prepared prep;   // garde jusqu'a la fin de l'envoi et des jonctions voisines
            public GaussianSplatAsset asset;
            public Pion pion;                    // objet de rendu (actif une fois envoye au GPU)
            public int split;                    // jonction : premiere gaussienne du cote a + 1
            public long octets;                  // donnees du modele (GPU ; meme poids en memoire tant que prep est garde)
            public int count;                    // gaussiennes
            public long fichier;                 // fichier compresse (octets)
        }

        class Pion
        {
            public GameObject go;
            public Gaussian4DSplatRenderer renderer;
            public Gaussian4DPlayer player;
        }

        // transition entre les sequences a et a + 1 : images debut..fin dessinees ensemble ; J = derniere image du cœur de a
        class Zone { public int a, debut, fin, J; }
        Zone[] m_Zones = Array.Empty<Zone>();
        readonly Dictionary<int, Entree> m_Entrees = new();
        readonly Stack<Pion> m_Pions = new();
        readonly List<Pion> m_TousPions = new();
        int m_Gen;
        int m_Capacite;
        float m_BudgetMB;
        int m_NbDecodeurs;
        double m_MsParMo = 2.0;
        float m_DureeImage = 1f / 60f;
        bool m_Demarre;              // lecture partie (regle de depart remplie)
        bool m_Attente;              // sequence voulue pas prete : derniere image tenue, temps et son arretes
        bool m_SonABoucler;          // bouclage : son relance a la premiere image dessinable
        double m_PremierDebut;       // debut de la premiere preparation (debit de preparation)
        int m_TriesAvant;            // Gaussian4DSplatRenderer.sortsSkipped a l'ouverture
        // ce qui a ete dessine a la derniere image (cles des entrees) : tenu a l'ecran pendant une attente
        readonly List<int> m_Affiches = new();
        int m_Ordre;
        float m_Opacite;

        Transform m_Scene;          // repere de la scene du site (pieds au sol) : cadrage et boite
        Transform m_Cadre;          // cadrage du rendu, parent des modeles
        GaussianCutout m_Boite;
        bool m_Cadree;
        readonly List<AudioSource> m_Audio = new();
        readonly List<string> m_Downloaded = new();   // fichiers telecharges pendant cette lecture
        static bool s_CachePurged;
        Coroutine m_Run;

        public void Open(RenduInfo info)
        {
            Close();
            // cache laisse par une session precedente (application fermee en pleine lecture) : vide au premier rendu
            if (!keepDownloads && !s_CachePurged) { PurgeCache(); s_CachePurged = true; }
            rendu = info;
            stats = new Stats();
            m_Run = StartCoroutine(Run());
        }

        public void Close()
        {
            if (m_Ouverture != null) { StopCoroutine(m_Ouverture); m_Ouverture = null; }
            if (m_Run != null) StopCoroutine(m_Run);
            m_Run = null;
            ++m_Gen;                                  // preparations en cours ignorees a leur fin
            foreach (var a in m_Audio) if (a) { a.Stop(); Destroy(a.clip); Destroy(a.gameObject); }
            m_Audio.Clear();
            m_AudioOn = false;
            foreach (var e in m_Entrees.Values) Liberer(e);
            m_Entrees.Clear();
            foreach (var p in m_TousPions) if (p.go) Destroy(p.go);
            m_TousPions.Clear();
            m_Pions.Clear();
            if (m_Scene) Destroy(m_Scene.gameObject);
            m_Boite = null;
            modelRadius = 0;
            if (!keepDownloads)
                foreach (var f in m_Downloaded) TryDelete(f);
            m_Downloaded.Clear();
            m_Capacite = 0; m_Demarre = false; m_Attente = false; m_SonABoucler = false; m_Paused = false;
            m_Affiches.Clear(); m_PremierDebut = 0;
            m_GpuSeq = m_DonneesSeq = m_FichierSeq = m_GpuJonction = 0;
            state = State.Idle;
            status = "";
        }

        void OnDestroy() => Close();

        float Duration => rendu.total / Mathf.Max(rendu.fps, 1);
        bool Mobile => Application.isMobilePlatform || SystemInfo.deviceType == DeviceType.Handheld;

        // ---- lecture : pause, reprise, deplacement (secondes du rendu)
        bool m_Paused;
        public void Pause()
        {
            if (m_Paused) return;
            m_Paused = true;
            StopAudio();
        }

        public void Play()
        {
            if (!m_Paused) return;
            m_Paused = false;
            if (state == State.Playing) StartAudio(m_Time);
            else Ancrer(m_Time, Reel);
        }

        public void Seek(float seconds)
        {
            if (rendu == null) return;
            m_Time = Mathf.Clamp(seconds, 0, Mathf.Max(0, Duration - 1e-4f));
            currentFrame = (float)(m_Time * rendu.fps);
            if (state == State.Playing && !m_Paused) StartAudio(m_Time);
            else Ancrer(m_Time, Reel);
        }

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
            m_Ordre = template.m_RenderOrder;
            m_Opacite = template.m_OpacityScale;
            // transitions : zones de fondu entre sequences voisines (toutes les sequences avec leurs images d'entrainement)
            var sq = rendu.sequences;
            bool fondu = crossfade && sq.Length > 1 && Array.TrueForAll(sq, x => x.trainImages > 0);
            var zones = new List<Zone>();
            if (fondu)
                for (int a = 0; a + 1 < sq.Length; ++a)
                {
                    var z = new Zone { a = a, debut = sq[a + 1].trainPremiere, fin = sq[a].trainPremiere + sq[a].trainImages - 1, J = sq[a].premiere + sq[a].images - 1 };
                    if (z.fin >= z.debut) zones.Add(z);
                }
            m_Zones = zones.ToArray();
            // appareil : budget memoire, preparations en parallele
            m_BudgetMB = memoryBudgetMB > 0 ? memoryBudgetMB : Mobile
                ? Mathf.Clamp(SystemInfo.systemMemorySize * 0.15f, 400, 1200)
                : Mathf.Clamp(Mathf.Min(SystemInfo.graphicsMemorySize * 0.5f, SystemInfo.systemMemorySize * 0.25f), 1000, 8000);
            m_NbDecodeurs = decoders > 0 ? decoders : Mobile ? 2 : Mathf.Clamp(SystemInfo.processorCount / 4, 2, 4);
            stats.decoders = m_NbDecodeurs;
            stats.budgetMB = m_BudgetMB;
            m_TriesAvant = Gaussian4DSplatRenderer.sortsSkipped;

            // son : toutes les pistes entierement chargees avant la lecture (un decodage au fil de l'eau decale le son) ;
            // la premiere sequence est preparee pendant ce temps
            m_Time = 0;
            Alimenter(0, 0, false);
            var pistes = rendu.sons_ogg != null && rendu.sons_ogg.Length > 0 ? rendu.sons_ogg : rendu.sons;
            if (rendu.sonsPlage != null && rendu.sonsPlage.Count > 0)
            {
                // .4dgs : chaque piste extraite dans le cache, puis lue comme un fichier local
                var locales = new List<string>();
                foreach (var sp in rendu.sonsPlage)
                {
                    status = "son…";
                    string chemin = CachePath(sp.url.Split('?')[0] + "_" + sp.offset) + Utils.Format4dgs.ExtensionSon(sp.type);
                    string err = null;
                    if (!File.Exists(chemin)) yield return Extraire(sp.url, sp.offset, sp.octets, chemin, e => err = e);
                    if (err != null) { Debug.LogWarning($"[Gaussian4DSequencePlayer] son du .4dgs illisible : {err}"); continue; }
                    m_Downloaded.Add(chemin);
                    locales.Add(chemin);
                }
                pistes = locales.ToArray();
            }
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

            var chrono = System.Diagnostics.Stopwatch.StartNew();
            double avant = chrono.Elapsed.TotalSeconds;
            while (true)
            {
                if (state == State.Error) yield break;
                double maintenant = chrono.Elapsed.TotalSeconds, ecart = maintenant - avant;
                float dt = (float)Math.Min(ecart, 0.1);
                avant = maintenant;
                if (dt > 0) m_DureeImage = 0.9f * m_DureeImage + 0.1f * dt;
                Image(dt, ecart);
                yield return null;
            }
        }

        // ---- une image : horloge, fenetre de sequences, envois au GPU, ce qui est dessine
        // ecart : secondes reelles depuis l'image precedente (gel)
        void Image(float dt, double ecart)
        {
            int n = rendu.sequences.Length;
            // gel (28/11/2026, comme la visionneuse du site) : plus de ~0,75 s (et de 4 durees d'image) sans image —
            // entree / sortie du mode immersif ou de l'AR, application en arriere-plan, casque retire — : le son est coupe
            // et repart de l'image affichee, le temps ne saute jamais (avant : il rattrapait d'un coup la duree du gel)
            if (m_Demarre && ecart > Math.Max(0.75, 4 * m_DureeImage)) ReprendreApresGel();
            // le temps n'avance que lecture partie, hors pause, et si la derniere image n'etait pas tenue
            if (m_Demarre && !m_Attente && !m_Paused)
            {
                Avancer(dt);
                if (m_Time >= Duration)
                {
                    // bouclage : le son repart avec la premiere image dessinable (plus bas), sinon il attend avec le temps
                    if (loop) { m_Time %= Duration; m_SonABoucler = true; }
                    else { m_Time = Duration - 1e-4; Pause(); state = State.Idle; status = "fin"; }
                }
            }
            float frame = (float)(m_Time * rendu.fps);
            int k = SequenceAt(frame);
            // derniere chance : ce qui doit etre dessine maintenant et n'est pas encore entierement envoye
            if (m_Demarre) FinirSiNecessaire(frame, k);
            bool dessinable = Dessinable(frame, k);
            if (!m_Demarre)
            {
                // depart selon le debit reel de preparation (premiere image deja dessinee pendant ce temps)
                if (dessinable && PeutDemarrer(k))
                {
                    m_Demarre = true; m_Attente = false; m_SonABoucler = false; state = State.Playing;
                    if (!m_Paused) StartAudio(m_Time); else Ancrer(m_Time, Reel);
                }
                else
                {
                    state = dessinable ? State.Waiting : State.Loading;
                    int prets = 0; var f0 = Fenetre(k);
                    foreach (int j in f0) if (Prete(j) != null) prets++;
                    status = dessinable ? $"mise en memoire {prets}/{f0.Count}…" : $"chargement de la sequence {k + 1}…";
                }
            }
            else if (!dessinable)
            {
                // sequence voulue pas prete : derniere image tenue, temps et son arretes (vitesse inchangee a la reprise)
                if (!m_Attente) { m_Attente = true; stats.waits++; StopAudio(); m_SonABoucler = false; state = State.Waiting; }
                if (m_Affiches.Count > 0) stats.heldFrames++;
                status = $"chargement de la sequence {k + 1}…";
            }
            else if (m_Attente || m_SonABoucler)
            {
                m_Attente = false; m_SonABoucler = false; state = State.Playing;
                if (!m_Paused) StartAudio(m_Time); else Ancrer(m_Time, Reel);
            }
            currentFrame = frame;
            stats.clockRealtime = m_ReelImage;

            // fenetre de sequences en avance (preparations, jonctions), envois au GPU par morceaux, une activation
            Alimenter(k, frame, !dessinable);
            Envoyer(k);
            stats.memoryMB = (float)MemoireEstimee();
            stats.keptAtStart = GardeesDebut();
            stats.sortsSkipped = Gaussian4DSplatRenderer.sortsSkipped - m_TriesAvant;
            if (!dessinable)
            {
                // image tenue : ce qui etait dessine reste a l'ecran tel quel (meme instant, memes poids, meme place), trie
                // a nouveau si la camera bouge ; jamais d'ecran vide pendant une attente
                foreach (var e in m_Entrees.Values)
                    if (e.etape == Etape.Pret && e.pion != null) e.pion.renderer.suspended = !m_Affiches.Contains(e.cle);
                return;
            }

            // ce qui est dessine : dans un recouvrement, sa jonction (les deux sequences, poids continus), sinon la
            // sequence k ; sans jonction prete, les deux sequences fondues l'une sur l'autre (repli)
            var zone = m_Zones.Length > 0 ? ZoneAt((int)frame) : null;
            var J = zone != null ? Prete(kJonction + zone.a) : null;
            float s = zone != null ? Mathf.Clamp01((frame - zone.debut) / Mathf.Max(1, zone.fin + 1 - zone.debut)) : 0;
            var A = zone != null && J == null ? Prete(zone.a) : null;
            var B = zone != null && J == null ? Prete(zone.a + 1) : null;
            bool repli = A != null && B != null;
            float u = frame / Mathf.Max(rendu.total - 1, 1);
            Vector3 marche = rendu.trajectoire != null ? rendu.trajectoire.A((float)m_Time, rendu.fps) : Vector3.zero;
            m_Affiches.Clear();
            foreach (var e in m_Entrees.Values)
            {
                if (e.etape != Etape.Pret || e.pion == null) continue;
                var r = e.pion.renderer;
                bool dessine;
                if (J != null) dessine = e == J;
                else if (repli) dessine = e == A || e == B;
                else dessine = !e.jonction && e.cle == k;
                r.suspended = !dessine;
                if (dessine) m_Affiches.Add(e.cle);
                e.pion.go.transform.localPosition = marche;      // trajectoire : avant le cadrage, repere du modele
                e.pion.player.previewTime = u;
                if (dessine && e == J) { r.junctionSplit = e.split; r.junctionWeights = new Vector2(1 - s, s); r.m_RenderOrder = m_Ordre; }
                else if (dessine && repli)
                {
                    // repli (jonction pas prete) : deux modeles tries separement, le plus faible dessine devant
                    float w = e == A ? 1 - s : s;
                    r.junctionSplit = int.MaxValue; r.junctionWeights = new Vector2(w, w);
                    r.m_RenderOrder = m_Ordre + (w < 0.5f ? 1 : 0);
                }
                else { r.junctionSplit = 0; r.junctionWeights = Vector2.one; r.m_RenderOrder = m_Ordre; }
            }
            if (m_Demarre)
                status = J != null ? $"sequences {zone.a + 1}-{zone.a + 2}/{n} (fondu) · image {(int)frame}/{rendu.total}"
                                   : $"sequence {k + 1}/{n} · image {(int)frame}/{rendu.total}";
        }

        Entree Prete(int cle) => m_Entrees.TryGetValue(cle, out var e) && e.etape == Etape.Pret && e.pion != null ? e : null;

        bool Dessinable(float frame, int k)
        {
            var z = m_Zones.Length > 0 ? ZoneAt((int)frame) : null;
            if (z != null && Prete(kJonction + z.a) != null) return true;
            return Prete(k) != null;
        }

        // ce qui doit etre dessine maintenant (jonction de la zone si elle est deja preparee, sinon la sequence) fini
        // d'un coup s'il est encore en cours d'envoi (compte : stats.forcedUploads)
        void FinirSiNecessaire(float frame, int k)
        {
            if (Dessinable(frame, k)) return;
            var z = m_Zones.Length > 0 ? ZoneAt((int)frame) : null;
            Entree e = null;
            if (z != null && m_Entrees.TryGetValue(kJonction + z.a, out var j) && j.etape == Etape.Envoi) e = j;
            else if (m_Entrees.TryGetValue(k, out var c) && c.etape == Etape.Envoi) e = c;
            if (e == null) return;
            e.asset.gpuData.Upload(long.MaxValue);
            stats.forcedUploads++;
            Activer(e);
        }

        int SequenceAt(float frame)
        {
            var seqs = rendu.sequences;
            for (int i = seqs.Length - 1; i >= 0; --i)
                if (frame >= seqs[i].premiere) return i;
            return 0;
        }

        Zone ZoneAt(int frame)
        {
            foreach (var z in m_Zones) if (frame >= z.debut && frame <= z.fin) return z;
            return null;
        }

        // secondes de lecture avant que l'entree soit necessaire (sens de lecture, boucle comprise) ; -1 : maintenant
        float NecessaireDans(Entree e, int k)
        {
            if (!e.jonction && e.cle == k) return -1;
            float debut;
            if (e.jonction) { var z = Array.Find(m_Zones, x => x.a == e.a); debut = z != null ? z.debut : 0; }
            else debut = e.cle > 0 && m_Zones.Length > 0 ? Mathf.Min(rendu.sequences[e.cle].premiere, rendu.sequences[e.cle].trainPremiere) : rendu.sequences[e.cle].premiere;
            float d = Mathf.Max(Duration, 1e-3f), t = debut / rendu.fps - (float)m_Time;
            return ((t % d) + d) % d;
        }

        int Capacite => Math.Max(2, m_Capacite > 0 ? m_Capacite : 2);

        // fin de boucle (28/11/2026, visionneuse du site 20261127b) : premieres sequences du rendu gardees en memoire
        // pendant toute la lecture, dans la fenetre (budget inchange : elles prennent des places de la fenetre, les autres
        // glissent sur ce qui reste) ; 2 a partir de 6 places, 1 a partir de 4, aucune en dessous ni quand tout tient
        int GardeesDebut()
        {
            int n = rendu.sequences.Length, cap = Capacite;
            return loop && n > cap ? (cap >= 6 ? 2 : cap >= 4 ? 1 : 0) : 0;
        }

        // sequences gardees a partir de k, dans l'ordre de lecture (boucle), premieres sequences gardees comprises
        List<int> Fenetre(int k)
        {
            int n = rendu.sequences.Length, cap = Math.Min(Capacite, n);
            var r = new List<int>(cap);
            if (!loop)
            {
                for (int j = k; j < n && r.Count < cap; j++) r.Add(j);
                return r;
            }
            int p = GardeesDebut();
            for (int s = 0; s < n && r.Count < cap; s++)
            {
                int j = (k + s) % n;
                int restantes = 0;                       // gardees du debut pas encore rencontrees
                for (int g = 0; g < p; g++) if (g != j && !r.Contains(g)) restantes++;
                if (j < p || r.Count + 1 + restantes <= cap) r.Add(j);
            }
            return r;
        }

        // fenetre ramenee a nb sequences : les plus lointaines partent d'abord, jamais la premiere ni les gardees du debut
        List<int> Raccourcir(List<int> fen, int nb)
        {
            var r = new List<int>(fen);
            int p = GardeesDebut();
            for (int j = r.Count - 1; r.Count > nb && j > 0; j--) if (r[j] >= p) r.RemoveAt(j);
            if (r.Count > nb) r.RemoveRange(nb, r.Count - nb);
            return r;
        }

        // Fenetre de sequences gardees a partir de k (ordre de lecture, boucle), jonctions entre elles et celle en
        // cours, et pendant une attente ce qui est tenu a l'ecran ; le reste libere ; preparations lancees dans cet
        // ordre (un telechargement a la fois, au plus m_NbDecodeurs preparations, chacune seulement si sa place tient
        // dans le budget memoire)
        void Alimenter(int k, float frame, bool tenue)
        {
            var fenetre = Fenetre(k);
            var fen = new HashSet<int>(fenetre);
            var garder = new HashSet<int>(fenetre);
            foreach (var z in m_Zones)
            {
                if (fen.Contains(z.a) && fen.Contains(z.a + 1)) garder.Add(kJonction + z.a);
                else if (fen.Contains(z.a + 1) && frame >= z.debut && frame < z.fin + 1) garder.Add(kJonction + z.a);   // fondu en cours
            }
            // image tenue (28/11/2026) : ce qui est dessine reste en memoire ; mobile (Quest) : pendant la tenue, pas de
            // nouveau chargement au-dela de capacite - tenues (le budget compte la sequence tenue)
            int tenues = 0;
            if (tenue) foreach (int c in m_Affiches) if (garder.Add(c)) tenues++;
            var charger = tenues > 0 && Mobile ? Raccourcir(fenetre, Math.Max(1, Capacite - tenues)) : fenetre;
            List<int> partir = null;
            foreach (var cle in m_Entrees.Keys) if (!garder.Contains(cle)) (partir ??= new List<int>()).Add(cle);
            if (partir != null)
                foreach (var cle in partir) { Liberer(m_Entrees[cle]); m_Entrees.Remove(cle); }

            // jonctions : des que les deux sequences voisines sont preparees
            foreach (var z in m_Zones)
            {
                int cj = kJonction + z.a;
                if (!garder.Contains(cj) || m_Entrees.ContainsKey(cj)) continue;
                if (m_Entrees.TryGetValue(z.a, out var ea) && m_Entrees.TryGetValue(z.a + 1, out var eb) && ea.prep != null && eb.prep != null
                    && EnCours() < m_NbDecodeurs)
                    StartCoroutine(PreparerJonction(z, ea.prep, eb.prep));
            }
            // donnees preparees inutiles : envoi fini et jonctions voisines commencees (ou sans objet)
            foreach (var e in m_Entrees.Values)
                if (!e.jonction && e.prep != null && e.etape == Etape.Pret && !JonctionAttendue(e.cle, garder)) e.prep = null;

            bool telechargement = false;
            foreach (var e in m_Entrees.Values) telechargement |= e.telechargement;
            if (telechargement || EnCours() >= m_NbDecodeurs) return;
            foreach (int j in charger)
                if (!m_Entrees.ContainsKey(j)) { if (PlacePourDecodage()) StartCoroutine(Preparer(j)); break; }
        }

        // ---- budget memoire (28/11/2026, visionneuse du site 20261127h) : poids reels de la sequence la plus lourde de
        // CE rendu. GPU : donnees du modele + tampons du rendu (vue 40 octets par gaussienne, tri 4 + 4 + 4 + 4, positions
        // deplacees 12) ; processeur : donnees preparees, gardees jusqu'a la fin de l'envoi (et des jonctions voisines),
        // et fichier compresse pendant le chargement. Estimation par etape : prete = GPU (+ donnees si gardees) ; envoi =
        // GPU reserve + donnees ; chargement = fichier + donnees decodees. La capacite laisse la place d'une sequence en
        // vol ; un decodage ne part que si sa place tient dans le budget.
        const int kOctetsRenduParGaussienne = 68;
        const double kMo = 1048576.0;
        long m_GpuSeq, m_DonneesSeq, m_FichierSeq, m_GpuJonction;

        static long PoidsGpu(Entree e) => e.octets + (long)kOctetsRenduParGaussienne * e.count;
        // place d'une sequence prete (Mo) : la sequence et, avec le fondu, une jonction (≈ une sequence)
        double MoParSeq => (m_GpuSeq + (m_Zones.Length > 0 ? (m_GpuJonction > 0 ? m_GpuJonction : m_GpuSeq) : 0)) / kMo;
        // une sequence en vol (chargement : fichier + donnees ; envoi : GPU + donnees), le plus lourd, au-dela de sa place
        double SurcoutVol => Math.Max(0, Math.Max(m_FichierSeq + m_DonneesSeq, m_GpuSeq + m_DonneesSeq) / kMo - MoParSeq);
        double MoEnVol => (m_FichierSeq + m_DonneesSeq) / kMo;
        bool Lourd => m_GpuSeq > 150 * kMo;

        double MemoireEntree(Entree e)
        {
            switch (e.etape)
            {
                case Etape.Chargement:
                    return e.jonction ? (m_GpuJonction > 0 ? m_GpuJonction : m_DonneesSeq) / kMo
                                      : ((e.fichier > 0 ? e.fichier : m_FichierSeq) + m_DonneesSeq) / kMo;
                case Etape.Envoi:
                    return (PoidsGpu(e) + e.octets) / kMo;
                default:
                    return e.asset != null ? (PoidsGpu(e) + (e.prep != null ? e.octets : 0)) / kMo : 0;
            }
        }

        // memoire estimee de tout ce que le lecteur tient maintenant (Mo)
        double MemoireEstimee()
        {
            double mo = 0;
            foreach (var e in m_Entrees.Values) mo += MemoireEntree(e);
            return mo;
        }

        // capacite recalculee a chaque modele prepare, sur la plus lourde vue
        void MajCapacite()
        {
            double p = MoParSeq;
            if (p <= 0) return;
            m_Capacite = (int)Math.Max(2, Math.Min(rendu.sequences.Length, Math.Floor((m_BudgetMB - SurcoutVol) / p)));
            stats.capacity = m_Capacite;
            stats.sequenceMB = (float)(m_GpuSeq / kMo);
        }

        // un decodage de plus : place reservee dans le budget ; mobile (Quest) : au plus une sequence en attente de son
        // envoi, et pour les sequences lourdes 2 decodages et 1 envoi en attente au plus
        bool PlacePourDecodage()
        {
            int enCours = 0, envois = 0, sequences = 0;
            long attente = 0;
            foreach (var e in m_Entrees.Values)
            {
                if (e.jonction) continue;
                sequences++;
                if (e.etape == Etape.Chargement) enCours++;
                else if (e.etape == Etape.Envoi) { envois++; if (e.asset?.gpuData != null) attente += e.asset.gpuData.remainingBytes; }
            }
            if (m_GpuSeq == 0) return enCours < (Mobile ? 2 : m_NbDecodeurs);      // poids pas encore connu
            // jamais bloque : la sequence voulue et la suivante se chargent toujours
            if (enCours == 0 && sequences < 2) return true;
            if (Mobile)
            {
                if (Lourd && (enCours >= 2 || envois > 1)) return false;
                if (attente > 1.05 * m_DonneesSeq) return false;
            }
            return MemoireEstimee() + MoEnVol <= m_BudgetMB;
        }

        // depart (28/11/2026, visionneuse du site 20261127g) : fenetre pleine, ou preparation au moins 1,25 fois plus
        // rapide que la lecture ET chaque sequence de la fenetre pas encore prete l'etant avant son tour au rythme mesure
        // (marge 15 % + 0,5 s) ; vitesse de lecture jamais changee
        bool PeutDemarrer(int k)
        {
            var fen = Fenetre(k);
            int n = rendu.sequences.Length, prets = 0;
            foreach (var e in m_Entrees.Values) if (!e.jonction && e.etape == Etape.Pret && e.pion != null) prets++;
            double s = m_PremierDebut > 0 ? Reel - m_PremierDebut : 0;
            float images = 0;
            foreach (var q in rendu.sequences) images += q.images;
            stats.prepRate = s > 0 ? (float)(prets / s) : 0;
            stats.playRate = rendu.fps / Mathf.Max(1, images / Mathf.Max(1, n));
            if (fen.TrueForAll(j => Prete(j) != null)) return true;
            if (stats.prepRate < 1.25f * stats.playRate) return false;
            double debut = 0;
            int manquantes = 0;
            for (int i = 0; i < fen.Count; i++)
            {
                if (i > 0) debut += rendu.sequences[fen[i - 1]].images / Math.Max(1.0, rendu.fps);
                if (Prete(fen[i]) != null) continue;
                manquantes++;
                if (1.15 * manquantes / stats.prepRate + 0.5 > debut) return false;
            }
            return true;
        }

        int EnCours()
        {
            int c = 0;
            foreach (var e in m_Entrees.Values) if (e.etape == Etape.Chargement) c++;
            return c;
        }

        // la sequence k doit-elle garder ses donnees preparees pour une jonction a venir ?
        bool JonctionAttendue(int k, HashSet<int> garder)
        {
            foreach (var z in m_Zones)
                if ((z.a == k || z.a + 1 == k) && garder.Contains(kJonction + z.a) && !m_Entrees.ContainsKey(kJonction + z.a)) return true;
            return false;
        }

        IEnumerator Preparer(int seq)
        {
            var e = new Entree { cle = seq, etape = Etape.Chargement, gen = m_Gen };
            m_Entrees[seq] = e;
            if (m_PremierDebut <= 0) m_PremierDebut = Reel;
            var info = rendu.sequences[seq];
            string path = info.url;
            if (info.octets > 0)
            {
                // sequence d'un .4dgs : sa plage d'octets, ecrite dans le cache comme un .ftgsz
                path = CachePath(info.url.Split('?')[0] + "_" + info.offset) + (info.format == "trgz" ? ".trgz" : ".ftgsz");   // HD 4DGS : .trgz
                if (!File.Exists(path))
                {
                    e.telechargement = true;
                    string err = null;
                    yield return Extraire(info.url, info.offset, info.octets, path, x => err = x);
                    e.telechargement = false;
                    if (e.gen != m_Gen) yield break;
                    if (err != null) { Fail($"sequence {seq + 1} : {err}"); yield break; }
                    m_Downloaded.Add(path);
                }
            }
            else if (IsRemote(info.url))
            {
                path = CachePath(info.url);
                if (!File.Exists(path))
                {
                    e.telechargement = true;
                    using var req = new UnityWebRequest(info.url, UnityWebRequest.kHttpVerbGET) { downloadHandler = new DownloadHandlerFile(path + ".part") { removeFileOnAbort = true } };
                    if (!string.IsNullOrEmpty(userAgent)) req.SetRequestHeader("User-Agent", userAgent);
                    yield return req.SendWebRequest();
                    e.telechargement = false;
                    if (e.gen != m_Gen) yield break;
                    if (req.result != UnityWebRequest.Result.Success) { Fail($"sequence {seq + 1} : {req.error}"); yield break; }
                    File.Move(path + ".part", path);
                    m_Downloaded.Add(path);
                }
            }
            try { e.fichier = info.octets > 0 ? info.octets : new FileInfo(path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(path).LocalPath : path).Length; }
            catch (Exception) { e.fichier = 0; }
            // preparation, recalage dans la video et fenetres sur un fil d'arriere-plan
            var chrono = System.Diagnostics.Stopwatch.StartNew();
            int total = rendu.total;
            bool zones = m_Zones.Length > 0;
            int maxSH = skipUnusedSH ? Mathf.Clamp(template.m_SHOrder, 0, 3) : 3;
            var task = GaussianSplatRuntimeLoader.PrepareAsync(path, quality, p =>
            {
                if (p.temporalFormat == GaussianSplatAsset.TemporalFormat.Trigs) RecalerTrigs(p, info, total);
                if (zones) ElargirFenetre(p, info, total);
                GaussianSplatRuntimeLoader.ComputePlayWindow(p);
            }, maxSH);
            while (!task.IsCompleted) yield return null;
            if (e.gen != m_Gen || !m_Entrees.TryGetValue(seq, out var deja) || deja != e) yield break;    // fermee ou liberee entre-temps
            if (task.IsFaulted) { Fail($"sequence {seq + 1} : {task.Exception?.GetBaseException().Message}"); yield break; }
            lastPrepareSeconds = (float)chrono.Elapsed.TotalSeconds;
            var prep = task.Result;
            if (!m_Cadree) { ApplyCadrage(prep.robustMin, prep.robustMax); m_Cadree = true; }
            e.prep = prep;
            Monter(e, prep);
        }

        IEnumerator PreparerJonction(Zone z, GaussianSplatRuntimeLoader.Prepared A, GaussianSplatRuntimeLoader.Prepared B)
        {
            int cle = kJonction + z.a;
            var e = new Entree { cle = cle, a = z.a, etape = Etape.Chargement, gen = m_Gen };
            m_Entrees[cle] = e;
            var sq = rendu.sequences;
            float frame = 1f / Mathf.Max(rendu.total - 1, 1);
            // fenetres des cotes : chacun depuis le debut de son entrainement / jusqu'a sa fin, l'autre borne sur la zone
            // (la jonction n'est dessinee que dedans) ; memes marges que les autres fenetres
            var zz = new GaussianSplatJunction.Zone
            {
                uStart = (z.debut - 0.5f) * frame, uEnd = (z.fin + 0.5f) * frame,
                winA0 = sq[z.a].trainPremiere * frame - 0.01f * frame, winA1 = z.fin * frame + 0.99f * frame,
                winB0 = z.debut * frame - 0.01f * frame, winB1 = (sq[z.a + 1].trainPremiere + sq[z.a + 1].trainImages - 1) * frame + 0.99f * frame,
            };
            int split = 0;
            var task = Task.Run(() => GaussianSplatJunction.Merge(A, B, zz, out split));
            while (!task.IsCompleted) yield return null;
            if (e.gen != m_Gen || !m_Entrees.TryGetValue(cle, out var deja) || deja != e) yield break;
            if (task.IsFaulted || task.Result == null)
            {
                // pas de jonction (formats differents, memoire) : repli sur les deux modeles separes
                if (task.IsFaulted) Debug.LogWarning($"[Gaussian4DSequencePlayer] jonction {z.a + 1}-{z.a + 2} : {task.Exception?.GetBaseException().Message}");
                e.etape = Etape.Pret;           // entree vide gardee : pas de nouvel essai
                yield break;
            }
            e.split = split;
            stats.junctions++;
            Monter(e, task.Result);
        }

        // donnees preparees -> asset dont l'envoi au GPU par morceaux commence (aucun envoi ici)
        void Monter(Entree e, GaussianSplatRuntimeLoader.Prepared p)
        {
            e.asset = GaussianSplatRuntimeLoader.CreateAssetGpu(p);
            e.octets = e.asset.gpuData.totalBytes;
            e.count = p.splatCount;
            e.etape = Etape.Envoi;
            // poids reels (le plus lourd vu) et capacite recalculee (28/11/2026)
            if (e.jonction) m_GpuJonction = Math.Max(m_GpuJonction, PoidsGpu(e));
            else
            {
                m_GpuSeq = Math.Max(m_GpuSeq, PoidsGpu(e));
                m_DonneesSeq = Math.Max(m_DonneesSeq, e.octets);
                m_FichierSeq = Math.Max(m_FichierSeq, e.fichier);
            }
            MajCapacite();
        }

        // Envois de l'image : morceaux des entrees en attente du GPU, la plus proche d'abord, dans un budget regle sur le
        // cout mesure (≈ uploadMsPerFrame ; bureau 8 a 96 Mo, mobile 4 a 32 Mo), augmente jusqu'a 3 fois quand la
        // prochaine risque de ne pas etre prete activateAhead s avant son tour (4 fois avant le depart) ; puis une
        // activation (renderer adopte les donnees) par image
        readonly List<Entree> m_Liste = new();
        void Envoyer(int k)
        {
            m_Liste.Clear();
            foreach (var e in m_Entrees.Values) if (e.etape == Etape.Envoi && e.asset != null) m_Liste.Add(e);
            if (m_Liste.Count == 0) { stats.uploadMs = 0; return; }
            m_Liste.Sort((x, y) => NecessaireDans(x, k).CompareTo(NecessaireDans(y, k)));
            const double Mo = 1048576.0;
            double lo = (Mobile ? 4 : 8) * Mo, hi = (Mobile ? 32 : 96) * Mo;
            // 28/11/2026 : duree visee uploadMsPerFrame, ou 15 % de la duree d'une image quand elles sont longues (10 ms au
            // plus) ; a 72 images/s, 4 ms comme avant
            double cible = Math.Max(uploadMsPerFrame, Math.Min(10, 150 * m_DureeImage));
            double normal = Math.Min(hi, Math.Max(lo, cible / Math.Max(1e-3, m_MsParMo) * Mo));
            double budget = m_Demarre ? normal : 4 * normal;
            if (m_Demarre && !m_Paused)
            {
                // rythme regle sur TOUTES les entrees en attente (28/11/2026, comme la visionneuse du site) : ce qui doit
                // etre sur le GPU avant la k-ieme (cumul, dans l'ordre ou la lecture les voudra), reparti sur les images
                // qui restent d'ici la ; toujours au plus 3 fois le budget normal
                double cumul = 0, voulu = 0;
                foreach (var e in m_Liste)
                {
                    cumul += e.asset.gpuData.remainingBytes;
                    double images = (NecessaireDans(e, k) - activateAhead) / Math.Max(1.0 / 120, m_DureeImage);
                    voulu = Math.Max(voulu, cumul / Math.Max(1, images));
                }
                budget = Math.Min(3 * normal, Math.Max(budget, voulu));
            }
            stats.uploadBudgetMB = (float)(budget / Mo);
            var chrono = System.Diagnostics.Stopwatch.StartNew();
            long fait = 0;
            Entree finie = null;
            foreach (var e in m_Liste)
            {
                if (fait >= budget) break;
                var g = e.asset.gpuData;
                if (!g.complete) fait += g.Upload((long)(budget - fait));
                if (g.complete && finie == null) finie = e;
            }
            double ms = chrono.Elapsed.TotalMilliseconds;
            stats.uploadMB += (float)(fait / Mo);
            stats.uploadMs = (float)ms;
            if (fait >= Mo) m_MsParMo = 0.8 * m_MsParMo + 0.2 * Math.Min(20, ms / (fait / Mo));
            // une activation par image (la plus proche entierement envoyee)
            if (finie != null) Activer(finie);
        }

        // modele entierement envoye : objet de rendu actif (adopte les donnees GPU), cache jusqu'a son tour
        void Activer(Entree e)
        {
            if (e.etape == Etape.Pret) return;
            var chrono = System.Diagnostics.Stopwatch.StartNew();
            var p = PrendrePion();
            e.pion = p;
            p.renderer.m_Asset = e.asset;
            p.renderer.suspended = true;
            p.renderer.junctionSplit = 0;
            p.renderer.junctionWeights = Vector2.one;
            p.renderer.m_OpacityScale = m_Opacite;
            p.player.temporalDataAsset = null;
            p.player.previewTime = currentFrame / Mathf.Max(rendu.total - 1, 1);
            p.go.SetActive(true);                                  // Gaussian4DSplatRenderer.OnEnable : adoption, sans envoi
            p.player.SetTemporalData(null);                        // buffer temporel de l'asset (deja sur le GPU)
            e.etape = Etape.Pret;
            stats.activateMs = (float)chrono.Elapsed.TotalMilliseconds;
        }

        Pion PrendrePion()
        {
            if (m_Pions.Count > 0) return m_Pions.Pop();
            var go = Instantiate(template.gameObject, m_Cadre, false);
            go.name = $"Sequence{m_TousPions.Count}";
            go.SetActive(false);
            // place et orientation : celles du lecteur (le modele peut avoir les siennes dans la scene)
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            var pl = go.GetComponent<Gaussian4DPlayer>();
            pl.playOnStart = false;
            pl.loop = false;
            pl.temporalDataAsset = null;
            var r = go.GetComponent<Gaussian4DSplatRenderer>();
            r.m_Asset = null;
            r.m_Cutouts = m_Boite ? new[] { m_Boite } : null;
            var pion = new Pion { go = go, renderer = r, player = pl };
            m_TousPions.Add(pion);
            return pion;
        }

        void Liberer(Entree e)
        {
            var chrono = System.Diagnostics.Stopwatch.StartNew();
            if (e.pion != null)
            {
                var p = e.pion;
                if (p.go)
                {
                    p.go.SetActive(false);
                    p.renderer.m_Asset = null;
                    p.renderer.suspended = false;
                    p.renderer.junctionSplit = 0;
                    p.renderer.junctionWeights = Vector2.one;
                    p.renderer.m_RenderOrder = m_Ordre;
                    m_Pions.Push(p);
                }
                e.pion = null;
            }
            GaussianSplatRuntimeLoader.DestroyAsset(e.asset);
            e.asset = null;
            e.prep = null;
            stats.releaseMs = (float)chrono.Elapsed.TotalMilliseconds;
        }

        // transition : chaque gaussienne porte la fenetre du cœur de sa sequence (seg_start / seg_end) ; elle est elargie
        // aux images d'entrainement pour que la sequence reste visible pendant ses transitions (meme marge que
        // GaussianSplatAssetBuilder : de f - 1 % a f + 99 % de frame). Fil d'arriere-plan.
        static void ElargirFenetre(GaussianSplatRuntimeLoader.Prepared p, SequenceInfo info, int total)
        {
            bool trigs = p.temporalFormat == GaussianSplatAsset.TemporalFormat.Trigs;
            if (info.trainImages <= 0 || p.temporal == null || (p.temporalFormat != GaussianSplatAsset.TemporalFormat.FreeTimeGS && !trigs)) return;
            int n = trigs ? total : p.temporalFrames > 1 ? p.temporalFrames : total;
            float frame = 1f / Mathf.Max(n - 1, 1);
            float a = info.trainPremiere * frame - 0.01f * frame, b = (info.trainPremiere + info.trainImages - 1) * frame + 0.99f * frame;
            int stride = trigs ? GaussianSplatAsset.kTemporalStrideTrigs : 32, ow = trigs ? 92 : 20;
            var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(new Span<byte>(p.temporal, 0, p.temporal.Length / stride * stride));
            int fs = stride / 4, fo = ow / 4;
            for (int i = fo; i + 1 < f.Length; i += fs) { f[i] = a; f[i + 1] = b; }
        }

        // HD 4DGS (.trgz) : place de la sequence dans la video (premiere image, nombre d'images du rendu) ecrite dans
        // chaque enregistrement (u0, tScale, fenetre) ; temps du modele = (temps video - u0) * tScale. Fil d'arriere-plan.
        static void RecalerTrigs(GaussianSplatRuntimeLoader.Prepared p, SequenceInfo info, int total)
        {
            if (p.temporal == null || total < 2) return;
            int stride = GaussianSplatAsset.kTemporalStrideTrigs;
            float frame = 1f / (total - 1);
            // transition (21/11/2026) : temps du modele compte depuis la premiere image d'ENTRAINEMENT
            float u0 = (info.trainImages > 0 ? info.trainPremiere : info.premiere) * frame;
            float a = info.premiere * frame - 0.01f * frame, b = (info.premiere + info.images - 1) * frame + 0.99f * frame;
            var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(new Span<byte>(p.temporal, 0, p.temporal.Length / stride * stride));
            for (int i = 0; i + 28 <= f.Length; i += 28)
            {
                float fpsModele = (p.temporalFrames - 1) / Mathf.Max(f[i + 25], 1e-6f);
                float tScale = (total - 1) / Mathf.Max(fpsModele, 1e-3f);
                f[i + 23] = a; f[i + 24] = b; f[i + 25] = tScale; f[i + 27] = u0;
            }
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

        // ---- horloge (25/11/2026, regle ITHACA « il faut rester constant ») : vitesse de lecture STRICTEMENT constante,
        // jamais d'acceleration ni de ralentissement, meme doux. Temps du rendu = ancre + (temps reel - ancre reelle),
        // pente 1. Avec le son : l'ancre est posee sur la position du son lissee (couples temps reel / dspTime des
        // 0,5 dernieres secondes, droite de pente 1 : moyenne des ecarts) ; recalage dur seulement au depart, a la boucle,
        // a la pause / reprise / au deplacement, ou si l'horloge s'ecarte de plus de 0,25 s du son lisse. Appareil lent :
        // des images sont sautees, la vitesse ne change pas. Sans son : temps reel. Le temps ne recule jamais.
        double m_Time;               // temps du rendu (s)
        bool m_AudioOn;
        double m_AudioDsp, m_AudioPos;     // depart programme des pistes (dspTime) et temps du rendu a ce depart
        double m_Ancre, m_AncreReel;       // m_Time = m_Ancre + (reel - m_AncreReel)
        double m_DernierDsp = -1, m_ReelImage;
        readonly Queue<(double reel, double dsp)> m_Couples = new();
        const double kFenetreLissage = 0.5, kEcartMax = 0.25;

        static double Reel => Time.realtimeSinceStartupAsDouble;

        // couples (temps reel, dspTime) a chaque changement de dspTime (avance par paquets), 0,5 s gardees
        void Echantillonner()
        {
            double dsp = AudioSettings.dspTime, r = Reel;
            if (dsp != m_DernierDsp) { m_DernierDsp = dsp; m_Couples.Enqueue((r, dsp)); }
            while (m_Couples.Count > 2 && r - m_Couples.Peek().reel > kFenetreLissage) m_Couples.Dequeue();
        }

        // dspTime lisse a l'instant reel r : droite de pente 1 ajustee sur les couples (moyenne des ecarts)
        double DspLisse(double r)
        {
            if (m_Couples.Count == 0) return AudioSettings.dspTime;
            double ecart = 0;
            foreach (var c in m_Couples) ecart += c.dsp - c.reel;
            return r + ecart / m_Couples.Count;
        }

        void Ancrer(double t, double reel) { m_Ancre = t; m_AncreReel = reel; }

        // depart des pistes : programmees a l'instant ou l'horloge (qui part tout de suite de renderTime, sans arret)
        // atteindra leur position ; aucune attente de l'image
        void StartAudio(double renderTime)
        {
            m_AudioOn = false;
            Echantillonner();
            double r = Reel;
            double debut = AudioSettings.dspTime + 0.05;          // depart commun de toutes les pistes
            double avance = Math.Max(0, debut - DspLisse(r));      // secondes de rendu jusqu'a ce depart
            double pos = renderTime + avance;
            foreach (var a in m_Audio)
            {
                if (!a) continue;
                a.Stop();
                double ts = pos + rendu.decalage_son;
                if (a.clip == null || ts >= a.clip.length) continue;
                a.time = (float)Math.Max(ts, 0);
                a.PlayScheduled(debut + Math.Max(-ts, 0));
                m_AudioOn = true;
            }
            m_AudioDsp = debut;
            m_AudioPos = pos;
            Ancrer(renderTime, r);
            m_Time = renderTime;
        }

        void StopAudio()
        {
            foreach (var a in m_Audio) if (a) a.Stop();
            m_AudioOn = false;
        }

        // apres un gel (> 0,75 s sans image) : son coupe et reprogramme a l'image affichee, horloge ancree maintenant ;
        // en pause ou pendant une attente, seulement l'ancre (le son est deja arrete)
        void ReprendreApresGel()
        {
            stats.clockResumes++;
            if (m_Attente || m_Paused || state != State.Playing) { StopAudio(); Ancrer(m_Time, Reel); return; }
            StartAudio(m_Time);
        }

        // temps du rendu correspondant au son entendu (position lissee)
        double Heard() => m_AudioPos + (DspLisse(Reel) - m_AudioDsp);

        void Avancer(float dt)
        {
            if (m_AudioOn && !m_Audio.Exists(a => a && a.isPlaying))
            {
                // fin des pistes : la suite au temps reel, sans changer de vitesse
                m_AudioOn = false;
            }
            double r = Reel;
            if (m_AudioOn)
            {
                Echantillonner();
                double horloge = m_Ancre + (r - m_AncreReel);
                double son = Heard();
                if (DspLisse(r) >= m_AudioDsp && Math.Abs(horloge - son) > kEcartMax)
                {
                    // l'horloge s'ecarte du son lisse de plus de 0,25 s : recalage dur (jamais en arriere)
                    Ancrer(Math.Max(m_Time, son), r);
                    stats.hardSyncs++;
                }
            }
            m_Time = Math.Max(m_Time, m_Ancre + (r - m_AncreReel));
            m_ReelImage = r;
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
