using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GaussianSplatting4D.Runtime.Utils
{
    // Lit les sequences 4DGS compressees ITHACA (.ftgsz v2 : FreeTimeGS ou 4C4D). Le fichier est decode en un PLY
    // identique de structure a l'original (memes champs, meme en-tete), ecrit une fois dans Library/Ftgsz/ (cache du projet, conserve entre les sessions) et relu ensuite
    // par le chemin PLY habituel (detection FreeTimeGS / 4C4D, qualite de l'asset, etc.).
    //
    // Fichier : "FTGSZ002", longueur de l'en-tete JSON (uint32 little-endian), en-tete JSON, flux bout a bout (gzip si
    // indique). Gaussiennes rangees par blocs (ordre de Morton). Codages :
    //   bloc    : entier de bits[j] bits ramene dans [min, max] du bloc (flux nom_bornes, float32 min..., max...),
    //             puis transfo inverse ("couleur" : (v - 0,5) / C0 ; "sigmoide" : logit) ;
    //   quat    : 3 composantes sur bits[0] bits dans [-1/sqrt2, 1/sqrt2] + numero (2 bits) de la plus grande, omise ;
    //   palette : indices sur bits[0] bits dans nom_palette (float16) ; table : indices dans nom_table (float32) ;
    //   zero    : champs non stockes (normales).
    // Les mots d'une gaussienne (octets_mot octets) sont stockes octet par octet : octet 0 de toutes, puis octet 1...
    // Reference : ftgsz.py (outils ITHACA), meme decodage dans le lecteur web (lecteur4d/ftgsz.js).
    public static class FtgszFileReader
    {
        const double C0 = 0.28209479177387814;

        [Serializable]
        public class Flux
        {
            public string nom;
            public List<string> champs;
            public string codage;
            public int octets;
            public int brut;
            public bool gzip;
            public List<int> bits;
            public int octets_mot;
            public List<string> transfo;
            public int lignes;
        }

        [Serializable]
        public class Entete
        {
            public string format;
            public int version;
            public string niveau;
            public int gaussiennes;
            public int bloc;
            public string ply;
            public List<Flux> flux;
            // HD 4DGS (.trgz, signature TRGSZ001, 21/11/2026) : memes flux, en-tete PLY reconstitue (EnteteTrgz)
            public int sh_degree, sh_source, images;
            public float fps_modele, coupure;
        }

        // .ftgsz, et .trgz (HD 4DGS : meme decodage, PLY aux champs mu, log_sigma, anchor_*, p0_*..p2_* lu ensuite
        // comme TemporalKind.Trigs)
        public static bool IsFtgsz(string filePath) => filePath.EndsWith(".ftgsz", true, CultureInfo.InvariantCulture)
                                                       || filePath.EndsWith(".trgz", true, CultureInfo.InvariantCulture);

        // Application : dossier du PLY decode, fixe sur le fil principal par GaussianSplatRuntimeLoader (les chemins de
        // Unity ne se lisent pas sur un autre fil) ; le PLY y est efface des qu'il a ete lu (DeleteAfterRead). Editeur :
        // null, Library/Ftgsz conserve.
        public static string runtimeCacheDir;
        public static bool DeleteAfterRead => runtimeCacheDir != null;
        // decodage en memoire (DecodeToMemory) meme dans l'editeur, sans PLY dans Library/Ftgsz (essais)
        public static bool inMemory;

        // Chemin du PLY decode (cree au premier appel, puis repris tant que le .ftgsz ne change pas)
        public static string ResolveToPly(string filePath)
        {
            var info = new FileInfo(filePath);
            string cle;
            using (var md5 = MD5.Create())
                cle = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(
                    $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"))).Replace("-", "").Substring(0, 16);
            // editeur : Library/Ftgsz du projet ; application : runtimeCacheDir (Library n'existe pas)
            string dossier = runtimeCacheDir ?? Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "Ftgsz");
            Directory.CreateDirectory(dossier);
            string sortie = Path.Combine(dossier, $"{Path.GetFileNameWithoutExtension(filePath)}_{cle}.ply");
            if (!File.Exists(sortie))
            {
                string partiel = sortie + ".partiel";
                File.WriteAllBytes(partiel, DecodeToPly(filePath));
                File.Move(partiel, sortie);
            }
            return sortie;
        }

        public static Entete ReadHeader(byte[] o, out int debutFlux)
        {
            string sig = o.Length >= 12 ? Encoding.ASCII.GetString(o, 0, 8) : "";
            if (sig != "FTGSZ002" && sig != "TRGSZ001")
                throw new IOException("Ce n'est pas un fichier .ftgsz (version 2) ni .trgz (version 1).");
            int lg = BitConverter.ToInt32(o, 8);
            var h = Encoding.UTF8.GetString(o, 12, lg).FromJson<Entete>();
            if (h != null && sig == "TRGSZ001") h.ply = EnteteTrgz(h);
            if (h == null || h.flux == null || string.IsNullOrEmpty(h.ply))
                throw new IOException("En-tete .ftgsz illisible.");
            debutFlux = 12 + lg;
            return h;
        }

        // En-tete PLY d'une sequence HD 4DGS : tous les coefficients f_rest de la source (absents = 0), puis les champs
        // propres au modele ; commentaires lus par GaussianFileReader.ParseFtgsHeader
        static string EnteteTrgz(Entete h)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("ply\nformat binary_little_endian 1.0\ncomment trgz_version 1\ncomment time_units normalized\n");
            sb.Append($"comment n_frames {(h.images > 1 ? h.images : 2)}\ncomment fps_modele {h.fps_modele.ToString(inv)}\n");
            sb.Append($"comment coupure {h.coupure.ToString(inv)}\nelement vertex {h.gaussiennes}\n");
            var champs = new List<string> { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2" };
            for (int i = 0; i < 3 * h.sh_source; i++) champs.Add($"f_rest_{i}");
            champs.AddRange(new[] { "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3", "mu", "log_sigma",
                                    "anchor_0", "anchor_1", "anchor_2" });
            for (int c = 0; c < 3; c++) for (int j = 0; j < 6; j++) champs.Add($"p{c}_{j}");
            foreach (var c in champs) sb.Append($"property float {c}\n");
            sb.Append("end_header\n");
            return sb.ToString();
        }

        public static byte[] DecodeToPly(string filePath)
        {
            byte[] o = File.ReadAllBytes(filePath);
            var h = ReadHeader(o, out int pos);
            byte[] plyHeader = Encoding.ASCII.GetBytes(h.ply);
            int nc = Champs(h.ply).Count;
            var sortie = new float[(long)h.gaussiennes * nc];
            unsafe { fixed (float* d = sortie) Decode(o, h, pos, d, 3); }
            var ply = new byte[plyHeader.Length + sortie.Length * 4L];
            Buffer.BlockCopy(plyHeader, 0, ply, 0, plyHeader.Length);
            Buffer.BlockCopy(sortie, 0, ply, plyHeader.Length, sortie.Length * 4);
            return ply;
        }

        // Decodage sans fichier intermediaire (24/11/2026) : en-tete PLY (octets) et sommets (memes octets que le PLY de
        // DecodeToPly) dans un tableau natif a liberer par l'appelant. maxSHDegree < 3 : flux des coefficients de degre
        // superieur ni decompresses ni decodes (laisses a 0).
        public static unsafe void DecodeToMemory(string filePath, int maxSHDegree, out byte[] plyHeader, out Unity.Collections.NativeArray<byte> vertices)
        {
            byte[] o = File.ReadAllBytes(filePath);
            var h = ReadHeader(o, out int pos);
            plyHeader = Encoding.ASCII.GetBytes(h.ply);
            int nc = Champs(h.ply).Count;
            vertices = new Unity.Collections.NativeArray<byte>((int)((long)h.gaussiennes * nc * 4), Unity.Collections.Allocator.Persistent, Unity.Collections.NativeArrayOptions.ClearMemory);
            try
            {
                Decode(o, h, pos, (float*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafePtr(vertices), maxSHDegree);
            }
            catch { vertices.Dispose(); throw; }
        }

        static List<string> Champs(string ply)
        {
            var champs = new List<string>();
            foreach (Match m in Regex.Matches(ply, @"property float (\S+)"))
                champs.Add(m.Groups[1].Value);
            return champs;
        }

        // degre des harmoniques d'un champ f_rest_i (rangement canal par canal, parCanal coefficients par canal)
        static int DegreSH(string champ, int parCanal)
        {
            if (!champ.StartsWith("f_rest_", StringComparison.Ordinal) || parCanal <= 0) return 0;
            int k = int.Parse(champ.Substring(7), CultureInfo.InvariantCulture) % parCanal;
            return k < 3 ? 1 : k < 8 ? 2 : 3;
        }

        // flux decodes dans sortie (n x nc floats, deja a zero)
        static unsafe void Decode(byte[] o, Entete h, int pos, float* sortie, int maxSHDegree)
        {
            int n = h.gaussiennes, bl = h.bloc;
            var champs = Champs(h.ply);
            int nc = champs.Count;
            var indice = new Dictionary<string, int>();
            for (int i = 0; i < nc; i++) indice[champs[i]] = i;
            int parCanal = 0;
            foreach (var c in champs) if (c.StartsWith("f_rest_", StringComparison.Ordinal)) parCanal++;
            parCanal /= 3;
            // flux inutiles : tous leurs champs sont des coefficients de degre > maxSHDegree (et leurs bornes)
            var inutiles = new HashSet<string>();
            if (maxSHDegree < 3)
                foreach (var f in h.flux)
                    if (f.champs != null && f.champs.Count > 0 && f.champs.TrueForAll(c => DegreSH(c, parCanal) > maxSHDegree))
                    {
                        inutiles.Add(f.nom);
                        inutiles.Add(f.nom + "_bornes"); inutiles.Add(f.nom + "_palette"); inutiles.Add(f.nom + "_table");
                    }

            // flux : non compresses lus en place dans le fichier, les autres decompresses une fois (24/11/2026 : sans
            // copie ni tableaux intermediaires par composante, moins de memoire a ramasser pendant la lecture)
            var donnees = new Dictionary<string, (byte[] a, int off)>();
            foreach (var f in h.flux)
            {
                if (!inutiles.Contains(f.nom))
                    donnees[f.nom] = f.gzip ? (Degzip(o, pos, f.octets, f.brut), 0) : (o, pos);
                pos += f.octets;
            }
            float[] Floats(string nom) { var d = donnees[nom]; return ToFloats(d.a, d.off, FluxOctets(h, nom)); }

            foreach (var f in h.flux)
            {
                if (inutiles.Contains(f.nom)) continue;
                switch (f.codage)
                {
                    case "bloc":
                    {
                        int k = f.champs.Count;
                        var bornes = Floats(f.nom + "_bornes");
                        var (d, off) = donnees[f.nom];
                        int m = f.octets_mot;
                        var c = new int[k]; var dec = new int[k]; var masque = new ulong[k]; var max = new double[k]; var mode = new int[k];
                        for (int j = 0, sh = 0; j < k; j++)
                        {
                            c[j] = indice[f.champs[j]];
                            dec[j] = sh; sh += f.bits[j];
                            masque[j] = (1UL << f.bits[j]) - 1;
                            max[j] = (1L << f.bits[j]) - 1;
                            string tr = f.transfo != null && j < f.transfo.Count ? f.transfo[j] : "";
                            mode[j] = tr == "couleur" ? 1 : tr == "sigmoide" ? 2 : 0;
                        }
                        for (int b0 = 0; b0 < n; b0 += bl)
                        {
                            int b = b0 / bl * 2 * k, fin = Math.Min(n, b0 + bl);
                            for (int i = b0; i < fin; i++)
                            {
                                ulong mot = Mot(d, off, n, m, i);
                                for (int j = 0; j < k; j++)
                                {
                                    // bornes du bloc ; memes calculs en double qu'avant
                                    double mn = bornes[b + j], mx = bornes[b + k + j];
                                    double v = mn + (long)((mot >> dec[j]) & masque[j]) / max[j] * (mx - mn);
                                    if (mode[j] == 1) v = (v - 0.5) / C0;
                                    else if (mode[j] == 2) { v = Math.Min(Math.Max(v, 1e-7), 1 - 1e-7); v = Math.Log(v / (1 - v)); }
                                    sortie[(long)i * nc + c[j]] = (float)v;
                                }
                            }
                        }
                        break;
                    }
                    case "quat":
                    {
                        int bq = f.bits[0];
                        double mq = (1L << bq) - 1, s2 = Math.Sqrt(0.5);
                        var (d, off) = donnees[f.nom];
                        ulong mk = (1UL << bq) - 1;
                        var c = new int[4];
                        for (int j = 0; j < 4; j++) c[j] = indice[f.champs[j]];
                        var q = new double[4];
                        for (int i = 0; i < n; i++)
                        {
                            ulong mot = Mot(d, off, n, f.octets_mot, i);
                            int im = (int)((mot >> (3 * bq)) & 3UL), a = 0;
                            double s = 0;
                            for (int j = 0; j < 4; j++)
                            {
                                if (j == im) continue;
                                q[j] = (long)((mot >> (a * bq)) & mk) / mq * 2 * s2 - s2;
                                s += q[j] * q[j];
                                a++;
                            }
                            q[im] = Math.Sqrt(Math.Max(0, 1 - s));
                            for (int j = 0; j < 4; j++) sortie[(long)i * nc + c[j]] = (float)q[j];
                        }
                        break;
                    }
                    case "palette":
                    case "table":
                    {
                        int k = f.champs.Count;
                        float[] t;
                        if (f.codage == "palette") { var pd = donnees[f.nom + "_palette"]; t = FromHalfs(pd.a, pd.off, FluxOctets(h, f.nom + "_palette")); }
                        else t = Floats(f.nom + "_table");
                        var c = new int[k];
                        for (int j = 0; j < k; j++) c[j] = indice[f.champs[j]];
                        bool avecIdx = f.bits[0] > 0;
                        var (d, off) = avecIdx ? donnees[f.nom] : (null, 0);
                        ulong mk = avecIdx ? (1UL << f.bits[0]) - 1 : 0;
                        for (int i = 0; i < n; i++)
                        {
                            long idx = avecIdx ? (long)(Mot(d, off, n, f.octets_mot, i) & mk) : 0;
                            for (int j = 0; j < k; j++)
                                sortie[(long)i * nc + c[j]] = t[idx * k + j];
                        }
                        break;
                    }
                    // "zero" : deja a 0 ; "donnees" : bornes, palettes, tables (lues ci-dessus)
                }
            }
        }

        static byte[] Degzip(byte[] o, int debut, int octets, int taille)
        {
            var r = new byte[taille];
            using var gz = new GZipStream(new MemoryStream(o, debut, octets, false), CompressionMode.Decompress);
            int lu = 0;
            while (lu < taille)
            {
                int k = gz.Read(r, lu, (int)Math.Min(taille - lu, 1 << 20));
                if (k <= 0) throw new IOException("Flux .ftgsz tronque.");
                lu += k;
            }
            return r;
        }

        // octets (decompresses) d'un flux
        static int FluxOctets(Entete h, string nom)
        {
            foreach (var f in h.flux) if (f.nom == nom) return f.gzip ? f.brut : f.octets;
            throw new IOException($"Flux {nom} absent du .ftgsz.");
        }

        // mot de m octets de la gaussienne i (octets stockes plan par plan), composantes des bits faibles aux forts
        static ulong Mot(byte[] d, int off, int n, int m, int i)
        {
            ulong mot = 0;
            for (int k = 0; k < m; k++) mot |= (ulong)d[off + (long)k * n + i] << (8 * k);
            return mot;
        }

        static float[] ToFloats(byte[] b, int off, int octets)
        {
            var f = new float[octets / 4];
            Buffer.BlockCopy(b, off, f, 0, f.Length * 4);
            return f;
        }

        static float[] FromHalfs(byte[] b, int off, int octets)
        {
            var f = new float[octets / 2];
            for (int i = 0; i < f.Length; i++) f[i] = Mathf.HalfToFloat(BitConverter.ToUInt16(b, off + 2 * i));
            return f;
        }
    }
}
