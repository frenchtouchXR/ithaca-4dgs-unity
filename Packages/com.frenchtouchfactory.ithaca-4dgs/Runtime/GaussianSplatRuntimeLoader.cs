// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading.Tasks;
using GaussianSplatting4D.Runtime.Utils;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting4D.Runtime
{
    // Chargement d'un modele (.ftgsz, .ply, .spz ; 3DGS, 4C4D ou FreeTimeGS) pendant l'execution, sans passer par
    // l'editeur : memes etapes et memes octets que le createur d'assets (GaussianSplatAssetBuilder), qualites sans
    // regroupement des SH ni compression BC7 (reservees a l'editeur).
    //   Prepare  : lecture, ordre de Morton, encodage ; utilisable sur un fil d'arriere-plan (PrepareAsync).
    //   CreateAsset : GaussianSplatAsset et ses donnees (TextAsset) ; fil principal.
    public static class GaussianSplatRuntimeLoader
    {
        public enum Quality { VeryHigh, High, Medium }

        public sealed class Prepared
        {
            public string name;
            public int splatCount;
            public GaussianSplatAsset.VectorFormat formatPos, formatScale;
            public GaussianSplatAsset.ColorFormat formatColor;
            public GaussianSplatAsset.SHFormat formatSH;
            public Vector3 boundsMin, boundsMax;
            public Vector3 robustMin, robustMax;   // centiles 1 et 99 % des positions (comme le lecteur du site)
            public GaussianSplatAsset.TemporalFormat temporalFormat;
            public int temporalFrames, temporalFirstFrame, temporalSegments;
            public bool isTemporal;
            public Hash128 dataHash;
            public byte[] chunk, pos, other, color, sh, temporal;
            // plage de lecture (fenetre des sequences, FreeTimeGS et HD 4DGS ; -1 : aucune), ComputePlayWindow
            public float playStart, playEnd = -1f;
        }

        // memes formats que les qualites du createur de l'editeur
        public static void GetFormats(Quality q, out GaussianSplatAsset.VectorFormat pos, out GaussianSplatAsset.VectorFormat scale,
            out GaussianSplatAsset.ColorFormat color, out GaussianSplatAsset.SHFormat sh)
        {
            switch (q)
            {
                case Quality.Medium:
                    pos = GaussianSplatAsset.VectorFormat.Norm11; scale = GaussianSplatAsset.VectorFormat.Norm11;
                    color = GaussianSplatAsset.ColorFormat.Norm8x4; sh = GaussianSplatAsset.SHFormat.Norm6;
                    break;
                case Quality.High:
                    pos = GaussianSplatAsset.VectorFormat.Norm16; scale = GaussianSplatAsset.VectorFormat.Norm16;
                    color = GaussianSplatAsset.ColorFormat.Float16x4; sh = GaussianSplatAsset.SHFormat.Norm11;
                    break;
                default:
                    pos = GaussianSplatAsset.VectorFormat.Float32; scale = GaussianSplatAsset.VectorFormat.Float32;
                    color = GaussianSplatAsset.ColorFormat.Float32x4; sh = GaussianSplatAsset.SHFormat.Float32;
                    break;
            }
        }

        // a appeler sur le fil principal : chemins de Unity lus ici, la preparation tourne ensuite en arriere-plan
        public static Task<Prepared> PrepareAsync(string filePath, Quality quality) => PrepareAsync(filePath, quality, null);

        // after : traitement des donnees preparees, sur le meme fil d'arriere-plan (ex. recalage temporel) ;
        // maxSHDegree < 3 : les flux de couleurs au-dela de ce degre ne sont ni decompresses ni decodes (.ftgsz, .trgz ;
        // coefficients laisses a 0, a n'utiliser que si le rendu ne les dessine pas : m_SHOrder <= maxSHDegree)
        public static Task<Prepared> PrepareAsync(string filePath, Quality quality, Action<Prepared> after, int maxSHDegree = 3)
        {
            if (!Application.isEditor)
                FtgszFileReader.runtimeCacheDir ??= Path.Combine(Application.temporaryCachePath, "Ftgsz");
            return Task.Run(() =>
            {
                var p = Prepare(filePath, quality, true, maxSHDegree);
                after?.Invoke(p);
                return p;
            });
        }

        // onCurrentThread : sans le systeme de jobs (obligatoire hors du fil principal)
        public static Prepared Prepare(string filePath, Quality quality, bool onCurrentThread) => Prepare(filePath, quality, onCurrentThread, 3);

        public static Prepared Prepare(string filePath, Quality quality, bool onCurrentThread, int maxSHDegree)
        {
            Prepared r = null;
            if (onCurrentThread)
                GaussianSplatAssetBuilder.RunOnCurrentThread(() => r = PrepareCore(filePath, quality, maxSHDegree));
            else
                r = PrepareCore(filePath, quality, maxSHDegree);
            return r;
        }

        // plage de lecture d'un modele FreeTimeGS / HD 4DGS : fenetres de sequence (win_start / win_end) de toutes ses
        // gaussiennes, bornees a [0, 1] ; meme regle que Gaussian4DPlayer. N'importe quel fil.
        public static void ComputePlayWindow(Prepared p)
        {
            p.playStart = 0f; p.playEnd = -1f;
            bool trigs = p.temporalFormat == GaussianSplatAsset.TemporalFormat.Trigs, ftgs = p.temporalFormat == GaussianSplatAsset.TemporalFormat.FreeTimeGS;
            if (p.temporal == null || !(trigs || ftgs)) return;
            int stride = trigs ? GaussianSplatAsset.kTemporalStrideTrigs : GaussianSplatAsset.kTemporalStrideFTGS, ow = trigs ? 92 : 20;
            int n = p.temporal.Length / stride;
            if (n == 0) return;
            float a = float.MaxValue, b = float.MinValue;
            var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(new Span<byte>(p.temporal, 0, n * stride));
            int fs = stride / 4, fo = ow / 4;
            for (int i = 0; i < n; i++)
            {
                a = Mathf.Min(a, f[i * fs + fo]);
                b = Mathf.Max(b, f[i * fs + fo + 1]);
            }
            a = Mathf.Max(a, 0f); b = Mathf.Min(b, 1f);
            if (b > a && (a > 0f || b < 1f)) { p.playStart = a; p.playEnd = b; }
        }

        static Prepared PrepareCore(string filePath, Quality quality, int maxSHDegree)
        {
            GaussianFileReader.ReadFile(filePath, out NativeArray<InputSplatData> splats, out TemporalKind kind, out FtgsHeaderInfo ftgs, maxSHDegree);
            try
            {
                var r = new Prepared { name = Path.GetFileNameWithoutExtension(filePath), splatCount = splats.Length };
                GetFormats(quality, out r.formatPos, out r.formatScale, out r.formatColor, out r.formatSH);
                RobustBounds(splats, out r.robustMin, out r.robustMax);
                GaussianSplatAssetBuilder.CalcBounds(splats, out float3 bMin, out float3 bMax);
                r.boundsMin = bMin; r.boundsMax = bMax;
                GaussianSplatAssetBuilder.ReorderMorton(splats, bMin, bMax);

                var hash = new Hash128((uint)splats.Length, (uint)GaussianSplatAsset.kCurrentVersion, 0, 0);
                // donnees temporelles avant les morceaux (qui reecrivent les echelles en place), comme le createur
                r.isTemporal = kind != TemporalKind.None;
                if (kind == TemporalKind.Trigs)                           // HD 4DGS (.trgz)
                {
                    r.temporal = Emit(s => GaussianSplatAssetBuilder.CreateTemporalDataTrigs(splats, s, ref hash, ftgs), (long)splats.Length * GaussianSplatAsset.kTemporalStrideTrigs);
                    r.temporalFormat = GaussianSplatAsset.TemporalFormat.Trigs;
                    r.temporalFrames = ftgs.nFrames; r.temporalFirstFrame = 0; r.temporalSegments = 1;
                }
                else if (kind == TemporalKind.FreeTimeGS)
                {
                    r.temporal = Emit(s => GaussianSplatAssetBuilder.CreateTemporalDataFTGS(splats, s, ref hash, ftgs), (long)splats.Length * GaussianSplatAsset.kTemporalStrideFTGS);
                    r.temporalFormat = GaussianSplatAsset.TemporalFormat.FreeTimeGS;
                    r.temporalFrames = ftgs.nFrames; r.temporalFirstFrame = ftgs.firstFrame; r.temporalSegments = ftgs.segments;
                }
                else      // 4C4D, et 3DGS statique comme dans le createur (buffer temporel inutilise)
                    r.temporal = Emit(s => GaussianSplatAssetBuilder.CreateTemporalData(splats, s, ref hash), (long)splats.Length * GaussianSplatAsset.kTemporalStride4C4D);
                if (GaussianSplatAssetBuilder.UsesChunks(r.formatPos, r.formatScale, r.formatColor, r.formatSH))
                    r.chunk = Emit(s => GaussianSplatAssetBuilder.CreateChunkData(splats, s, ref hash), GaussianSplatAsset.CalcChunkDataSize(splats.Length));
                int n = splats.Length;
                r.pos = Emit(s => GaussianSplatAssetBuilder.CreatePositionsData(splats, r.formatPos, s, ref hash), Multiple8(GaussianSplatAsset.CalcPosDataSize(n, r.formatPos)));
                r.other = Emit(s => GaussianSplatAssetBuilder.CreateOtherData(splats, r.formatScale, s, ref hash, default), Multiple8(GaussianSplatAsset.CalcOtherDataSize(n, r.formatScale)));
                r.color = Emit(s => GaussianSplatAssetBuilder.CreateColorData(splats, r.formatColor, s, ref hash), GaussianSplatAsset.CalcColorDataSize(n, r.formatColor));
                r.sh = Emit(s => GaussianSplatAssetBuilder.CreateSHData(splats, r.formatSH, s, ref hash, default), GaussianSplatAsset.CalcSHDataSize(n, r.formatSH));
                r.dataHash = hash;
                return r;
            }
            finally
            {
                splats.Dispose();
            }
        }

        // boundsForModel du lecteur du site (ftgs.js) : centiles 1 et 99 % sur au plus 8192 gaussiennes
        static void RobustBounds(NativeArray<InputSplatData> splats, out Vector3 lo, out Vector3 hi)
        {
            int n = splats.Length, pas = Math.Max(1, n / 8192), m = (n + pas - 1) / pas;
            lo = hi = Vector3.zero;
            if (n == 0) return;
            var v = new float[m];
            for (int axe = 0; axe < 3; ++axe)
            {
                for (int i = 0, k = 0; i < n; i += pas, ++k) v[k] = splats[i].pos[axe];
                Array.Sort(v);
                lo[axe] = v[(int)((m - 1) * 0.01)];
                hi[axe] = v[(int)Math.Ceiling((m - 1) * 0.99)];
            }
        }

        static long Multiple8(long v) => (v + 7) / 8 * 8;

        // taille attendue connue : tableau final alloue une fois (sans agrandissements ni copie finale)
        static byte[] Emit(Action<Stream> write, long attendu = 0)
        {
            using var ms = attendu > 0 && attendu < int.MaxValue ? new MemoryStream((int)attendu) : new MemoryStream();
            write(ms);
            if (ms.Length == ms.Capacity && ms.TryGetBuffer(out var seg) && seg.Offset == 0 && seg.Array.Length == ms.Length) return seg.Array;
            return ms.ToArray();
        }

        static TextAsset Data(byte[] b, string name) => b == null ? null : new TextAsset(new ReadOnlySpan<byte>(b)) { name = name };

        // Fil principal. Les TextAsset sont des sous-objets a detruire avec l'asset (DestroyAsset).
        public static GaussianSplatAsset CreateAsset(Prepared p)
        {
            var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            asset.name = p.name;
            asset.Initialize(p.splatCount, p.formatPos, p.formatScale, p.formatColor, p.formatSH, p.boundsMin, p.boundsMax, null);
            if (p.temporalFormat == GaussianSplatAsset.TemporalFormat.FreeTimeGS || p.temporalFormat == GaussianSplatAsset.TemporalFormat.Trigs)
                asset.SetTemporalInfo(p.temporalFormat, p.temporalFrames, p.temporalFirstFrame, p.temporalSegments);
            asset.SetDataHash(p.dataHash);
            asset.SetAssetFiles(Data(p.chunk, p.name + "_chk"), Data(p.pos, p.name + "_pos"), Data(p.other, p.name + "_oth"),
                Data(p.color, p.name + "_col"), Data(p.sh, p.name + "_shs"), Data(p.temporal, p.name + "_tmp"));
            return asset;
        }

        // Fil principal : asset dont les donnees sont envoyees au GPU par morceaux (asset.gpuData.Upload a chaque image,
        // jusqu'a complete) au lieu d'etre copiees dans des TextAsset puis envoyees d'un bloc a l'activation du rendu.
        // Les tableaux de p sont lus jusqu'a la fin des envois.
        public static GaussianSplatAsset CreateAssetGpu(Prepared p)
        {
            var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            asset.name = p.name;
            asset.Initialize(p.splatCount, p.formatPos, p.formatScale, p.formatColor, p.formatSH, p.boundsMin, p.boundsMax, null);
            if (p.temporalFormat == GaussianSplatAsset.TemporalFormat.FreeTimeGS || p.temporalFormat == GaussianSplatAsset.TemporalFormat.Trigs)
                asset.SetTemporalInfo(p.temporalFormat, p.temporalFrames, p.temporalFirstFrame, p.temporalSegments);
            asset.SetDataHash(p.dataHash);
            asset.gpuData = GaussianSplatGpuData.Create(p);
            asset.gpuData.playStart = p.playStart;
            asset.gpuData.playEnd = p.playEnd;
            return asset;
        }

        public static void DestroyAsset(GaussianSplatAsset asset)
        {
            if (asset == null) return;
            asset.gpuData?.Dispose();
            asset.gpuData = null;
            foreach (var t in new[] { asset.chunkData, asset.posData, asset.otherData, asset.colorData, asset.shData, asset.temporalData })
                if (t != null) UnityEngine.Object.Destroy(t);
            UnityEngine.Object.Destroy(asset);
        }
    }
}
