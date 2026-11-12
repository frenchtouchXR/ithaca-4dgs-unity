// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace GaussianSplatting4D.Runtime.Utils
{
    // Format .4dgs v2 (10/11/2026) : une video 4D ITHACA en un seul fichier. Sequences .ftgsz v2 bout a bout, pistes son,
    // vignette, puis un index JSON ; signature « ITHACA4D » au debut (avec la version, uint16) et a la fin :
    //   [« ITHACA4D »][version][6 octets reserves] blocs… [index JSON][position de l'index uint64][longueur uint32][« ITHACA4D »]
    // Specification : docs/FORMAT_4DGS.md du depot du pipeline ITHACA. Chaque sequence est un .ftgsz complet : elle se
    // decode avec FtgszFileReader une fois extraite (plage d'octets du fichier).
    public static class Format4dgs
    {
        public const string Signature = "ITHACA4D";
        public const int Version = 2;
        public const int TailleDebut = 16;     // signature + version + reserve
        public const int TailleFin = 20;       // position de l'index + longueur + signature
        public const int Queue = 65536;        // octets lus a la fin du fichier pour trouver l'index (le plus souvent assez)

        [Serializable]
        public class Bloc
        {
            public long offset;
            public long octets;
            public string type;          // sons, vignette : type MIME
            public string nom;           // sons
            public int premiere, images, gaussiennes;   // sequences
            public int entrainement_premiere, entrainement_images;   // sequences avec transition (12/11/2026) ; 0 = sans
            public string encodage;      // sequences : « ftgsz2 »
        }

        [Serializable]
        public class Index
        {
            public string format;
            public int version;
            public string titre;
            public float fps = 30;
            public int images;
            public float decalage_son;
            public Gaussian4DSequencePlayer.Cadrage cadrage;
            public Bloc[] sequences;
            public Bloc[] sons;
            public Bloc vignette;
        }

        public static bool Est4dgs(string url) => !string.IsNullOrEmpty(url) && Path.GetExtension(url.Split('?')[0]).Equals(".4dgs", StringComparison.OrdinalIgnoreCase);

        // Fin du fichier (au moins TailleFin octets) : position et longueur de l'index ; faux si la signature manque
        public static bool LireFin(byte[] fin, long tailleFichier, out long position, out int longueur)
        {
            position = 0; longueur = 0;
            if (fin == null || fin.Length < TailleFin) return false;
            int k = fin.Length - TailleFin;
            if (Encoding.ASCII.GetString(fin, fin.Length - 8, 8) != Signature) return false;
            position = (long)BitConverter.ToUInt64(fin, k);
            longueur = (int)BitConverter.ToUInt32(fin, k + 8);
            return position >= TailleDebut && position + longueur == tailleFichier - TailleFin;
        }

        public static bool LireDebut(byte[] debut) =>
            debut != null && debut.Length >= 10 && Encoding.ASCII.GetString(debut, 0, 8) == Signature && BitConverter.ToUInt16(debut, 8) == Version;

        // Index (texte JSON) d'un fichier .4dgs sur le disque, apres controle des signatures
        public static string LireIndexLocal(string chemin)
        {
            using var f = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.Read);
            var debut = new byte[TailleDebut];
            if (f.Read(debut, 0, debut.Length) != debut.Length || !LireDebut(debut))
                throw new IOException($"{chemin} : pas un fichier .4dgs v{Version}");
            var fin = new byte[TailleFin];
            f.Seek(-TailleFin, SeekOrigin.End);
            f.Read(fin, 0, fin.Length);
            if (!LireFin(fin, f.Length, out long pos, out int lg))
                throw new IOException($"{chemin} : fichier .4dgs tronque ou incoherent");
            var texte = new byte[lg];
            f.Seek(pos, SeekOrigin.Begin);
            f.Read(texte, 0, lg);
            return Encoding.UTF8.GetString(texte);
        }

        public static Index Analyser(string json)
        {
            var idx = JsonUtility.FromJson<Index>(json);
            if (idx == null || idx.format != "4dgs" || idx.version != Version || idx.sequences == null || idx.sequences.Length == 0)
                throw new IOException("index .4dgs illisible");
            return idx;
        }

        // Plage d'octets d'un fichier local, ecrite dans un autre fichier (sequence ou son extrait)
        public static void ExtraireLocal(string chemin, long offset, long octets, string sortie)
        {
            using var f = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var o = new FileStream(sortie, FileMode.Create, FileAccess.Write);
            f.Seek(offset, SeekOrigin.Begin);
            var tampon = new byte[1 << 20];
            for (long reste = octets; reste > 0;)
            {
                int n = f.Read(tampon, 0, (int)Math.Min(tampon.Length, reste));
                if (n <= 0) throw new IOException($"{chemin} : fin de fichier avant la fin du bloc");
                o.Write(tampon, 0, n);
                reste -= n;
            }
        }

        // Extension de fichier d'un type de son (lecture par UnityWebRequestMultimedia)
        public static string ExtensionSon(string type) => type switch
        {
            "audio/ogg" => ".ogg",
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "audio/mp4" => ".m4a",
            "audio/aac" => ".aac",
            _ => ".bin",
        };

        // Preference de lecture dans Unity : Ogg Vorbis partout (le m4a n'est pas lu sur Android), puis mp3, wav, m4a
        public static int RangSon(string type) => type switch
        {
            "audio/ogg" => 0,
            "audio/mpeg" => 1,
            "audio/wav" => 2,
            "audio/mp4" => 3,
            "audio/aac" => 4,
            _ => 9,
        };
    }
}
