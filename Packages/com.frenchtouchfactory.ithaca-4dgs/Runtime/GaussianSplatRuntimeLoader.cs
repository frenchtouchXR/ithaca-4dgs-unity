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
        public static Task<Prepared> PrepareAsync(string filePath, Quality quality)
        {
            if (!Application.isEditor)
                FtgszFileReader.runtimeCacheDir ??= Path.Combine(Application.temporaryCachePath, "Ftgsz");
            return Task.Run(() => Prepare(filePath, quality, true));
        }

        // onCurrentThread : sans le systeme de jobs (obligatoire hors du fil principal)
        public static Prepared Prepare(string filePath, Quality quality, bool onCurrentThread)
        {
            Prepared r = null;
            if (onCurrentThread)
                GaussianSplatAssetBuilder.RunOnCurrentThread(() => r = PrepareCore(filePath, quality));
            else
                r = PrepareCore(filePath, quality);
            return r;
        }

        static Prepared PrepareCore(string filePath, Quality quality)
        {
            GaussianFileReader.ReadFile(filePath, out NativeArray<InputSplatData> splats, out TemporalKind kind, out FtgsHeaderInfo ftgs);
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
                if (kind == TemporalKind.FreeTimeGS)
                {
                    r.temporal = Emit(s => GaussianSplatAssetBuilder.CreateTemporalDataFTGS(splats, s, ref hash, ftgs));
                    r.temporalFormat = GaussianSplatAsset.TemporalFormat.FreeTimeGS;
                    r.temporalFrames = ftgs.nFrames; r.temporalFirstFrame = ftgs.firstFrame; r.temporalSegments = ftgs.segments;
                }
                else      // 4C4D, et 3DGS statique comme dans le createur (buffer temporel inutilise)
                    r.temporal = Emit(s => GaussianSplatAssetBuilder.CreateTemporalData(splats, s, ref hash));
                if (GaussianSplatAssetBuilder.UsesChunks(r.formatPos, r.formatScale, r.formatColor, r.formatSH))
                    r.chunk = Emit(s => GaussianSplatAssetBuilder.CreateChunkData(splats, s, ref hash));
                r.pos = Emit(s => GaussianSplatAssetBuilder.CreatePositionsData(splats, r.formatPos, s, ref hash));
                r.other = Emit(s => GaussianSplatAssetBuilder.CreateOtherData(splats, r.formatScale, s, ref hash, default));
                r.color = Emit(s => GaussianSplatAssetBuilder.CreateColorData(splats, r.formatColor, s, ref hash));
                r.sh = Emit(s => GaussianSplatAssetBuilder.CreateSHData(splats, r.formatSH, s, ref hash, default));
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

        static byte[] Emit(Action<Stream> write)
        {
            using var ms = new MemoryStream();
            write(ms);
            return ms.ToArray();
        }

        static TextAsset Data(byte[] b, string name) => b == null ? null : new TextAsset(new ReadOnlySpan<byte>(b)) { name = name };

        // Fil principal. Les TextAsset sont des sous-objets a detruire avec l'asset (DestroyAsset).
        public static GaussianSplatAsset CreateAsset(Prepared p)
        {
            var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            asset.name = p.name;
            asset.Initialize(p.splatCount, p.formatPos, p.formatScale, p.formatColor, p.formatSH, p.boundsMin, p.boundsMax, null);
            if (p.temporalFormat == GaussianSplatAsset.TemporalFormat.FreeTimeGS)
                asset.SetTemporalInfo(p.temporalFormat, p.temporalFrames, p.temporalFirstFrame, p.temporalSegments);
            asset.SetDataHash(p.dataHash);
            asset.SetAssetFiles(Data(p.chunk, p.name + "_chk"), Data(p.pos, p.name + "_pos"), Data(p.other, p.name + "_oth"),
                Data(p.color, p.name + "_col"), Data(p.sh, p.name + "_shs"), Data(p.temporal, p.name + "_tmp"));
            return asset;
        }

        public static void DestroyAsset(GaussianSplatAsset asset)
        {
            if (asset == null) return;
            foreach (var t in new[] { asset.chunkData, asset.posData, asset.otherData, asset.colorData, asset.shData, asset.temporalData })
                if (t != null) UnityEngine.Object.Destroy(t);
            UnityEngine.Object.Destroy(asset);
        }
    }
}
