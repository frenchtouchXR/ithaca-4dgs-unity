using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GaussianSplatting4D.Editor.Utils
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
        }

        public static bool IsFtgsz(string filePath) => filePath.EndsWith(".ftgsz", true, CultureInfo.InvariantCulture);

        // Chemin du PLY decode (cree au premier appel, puis repris tant que le .ftgsz ne change pas)
        public static string ResolveToPly(string filePath)
        {
            var info = new FileInfo(filePath);
            string cle;
            using (var md5 = MD5.Create())
                cle = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(
                    $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"))).Replace("-", "").Substring(0, 16);
            string dossier = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "Ftgsz");
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
            if (o.Length < 12 || Encoding.ASCII.GetString(o, 0, 8) != "FTGSZ002")
                throw new IOException("Ce n'est pas un fichier .ftgsz (version 2).");
            int lg = BitConverter.ToInt32(o, 8);
            var h = Encoding.UTF8.GetString(o, 12, lg).FromJson<Entete>();
            if (h == null || h.flux == null || string.IsNullOrEmpty(h.ply))
                throw new IOException("En-tete .ftgsz illisible.");
            debutFlux = 12 + lg;
            return h;
        }

        public static byte[] DecodeToPly(string filePath)
        {
            byte[] o = File.ReadAllBytes(filePath);
            var h = ReadHeader(o, out int pos);
            int n = h.gaussiennes, bl = h.bloc;
            byte[] plyHeader = Encoding.ASCII.GetBytes(h.ply);
            var champs = new List<string>();
            foreach (Match m in Regex.Matches(h.ply, @"property float (\S+)"))
                champs.Add(m.Groups[1].Value);
            int nc = champs.Count;
            var indice = new Dictionary<string, int>();
            for (int i = 0; i < nc; i++) indice[champs[i]] = i;

            var donnees = new Dictionary<string, byte[]>();
            foreach (var f in h.flux)
            {
                var brut = new byte[f.octets];
                Buffer.BlockCopy(o, pos, brut, 0, f.octets);
                pos += f.octets;
                donnees[f.nom] = f.gzip ? Degzip(brut, f.brut) : brut;
            }

            var sortie = new float[(long)n * nc];
            foreach (var f in h.flux)
            {
                switch (f.codage)
                {
                    case "bloc":
                    {
                        int k = f.champs.Count;
                        var bornes = ToFloats(donnees[f.nom + "_bornes"]);
                        var ent = Deballer(donnees[f.nom], n, f.bits, f.octets_mot);
                        for (int j = 0; j < k; j++)
                        {
                            int c = indice[f.champs[j]];
                            double max = (1L << f.bits[j]) - 1;
                            string tr = f.transfo != null && j < f.transfo.Count ? f.transfo[j] : "";
                            for (int i = 0; i < n; i++)
                            {
                                int b = i / bl * 2 * k;
                                double mn = bornes[b + j], mx = bornes[b + k + j];
                                double v = mn + ent[j][i] / max * (mx - mn);
                                if (tr == "couleur") v = (v - 0.5) / C0;
                                else if (tr == "sigmoide") { v = Math.Min(Math.Max(v, 1e-7), 1 - 1e-7); v = Math.Log(v / (1 - v)); }
                                sortie[(long)i * nc + c] = (float)v;
                            }
                        }
                        break;
                    }
                    case "quat":
                    {
                        int bq = f.bits[0];
                        double m = (1L << bq) - 1, s2 = Math.Sqrt(0.5);
                        var ent = Deballer(donnees[f.nom], n, new List<int> { bq, bq, bq, 2 }, f.octets_mot);
                        var c = new int[4];
                        for (int j = 0; j < 4; j++) c[j] = indice[f.champs[j]];
                        var q = new double[4];
                        for (int i = 0; i < n; i++)
                        {
                            int im = (int)ent[3][i], a = 0;
                            double s = 0;
                            for (int j = 0; j < 4; j++)
                            {
                                if (j == im) continue;
                                q[j] = ent[a][i] / m * 2 * s2 - s2;
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
                        float[] t = f.codage == "palette" ? FromHalfs(donnees[f.nom + "_palette"]) : ToFloats(donnees[f.nom + "_table"]);
                        long[] idx = f.bits[0] > 0 ? Deballer(donnees[f.nom], n, f.bits, f.octets_mot)[0] : new long[n];
                        var c = new int[k];
                        for (int j = 0; j < k; j++) c[j] = indice[f.champs[j]];
                        for (int i = 0; i < n; i++)
                            for (int j = 0; j < k; j++)
                                sortie[(long)i * nc + c[j]] = t[idx[i] * k + j];
                        break;
                    }
                    // "zero" : deja a 0 ; "donnees" : bornes, palettes, tables (lues ci-dessus)
                }
            }

            var ply = new byte[plyHeader.Length + sortie.Length * 4L];
            Buffer.BlockCopy(plyHeader, 0, ply, 0, plyHeader.Length);
            Buffer.BlockCopy(sortie, 0, ply, plyHeader.Length, sortie.Length * 4);
            return ply;
        }

        static byte[] Degzip(byte[] z, int taille)
        {
            var r = new byte[taille];
            using var gz = new GZipStream(new MemoryStream(z), CompressionMode.Decompress);
            int lu = 0;
            while (lu < taille)
            {
                int k = gz.Read(r, lu, (int)Math.Min(taille - lu, 1 << 20));
                if (k <= 0) throw new IOException("Flux .ftgsz tronque.");
                lu += k;
            }
            return r;
        }

        // Entiers de chaque composante : mot de m octets (octets stockes plan par plan), composantes des bits faibles aux forts
        static long[][] Deballer(byte[] octets, int n, List<int> bits, int m)
        {
            var r = new long[bits.Count][];
            for (int j = 0; j < bits.Count; j++) r[j] = new long[n];
            for (int i = 0; i < n; i++)
            {
                ulong mot = 0;
                for (int k = 0; k < m; k++) mot |= (ulong)octets[(long)k * n + i] << (8 * k);
                int dec = 0;
                for (int j = 0; j < bits.Count; j++)
                {
                    r[j][i] = (long)((mot >> dec) & ((1UL << bits[j]) - 1));
                    dec += bits[j];
                }
            }
            return r;
        }

        static float[] ToFloats(byte[] b)
        {
            var f = new float[b.Length / 4];
            Buffer.BlockCopy(b, 0, f, 0, f.Length * 4);
            return f;
        }

        static float[] FromHalfs(byte[] b)
        {
            var f = new float[b.Length / 2];
            for (int i = 0; i < f.Length; i++) f[i] = Mathf.HalfToFloat(BitConverter.ToUInt16(b, 2 * i));
            return f;
        }
    }
}
