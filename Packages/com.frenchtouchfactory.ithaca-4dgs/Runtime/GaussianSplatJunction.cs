// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting4D.Runtime
{
    // Modele de jonction du fondu enchaine entre deux sequences voisines (rendus avec transition, 24/11/2026, comme le
    // lecteur JS 0.4.0) : pendant le recouvrement des sequences a et a + 1, les deux sont dessinees comme UN seul modele,
    // trie d'un bloc (bon ordre de profondeur entre elles) ; gaussiennes [0, split) du cote a, les suivantes du cote
    // a + 1, ponderees dans le shader (Gaussian4DSplatRenderer.junctionSplit / junctionWeights).
    // HD 4DGS : seulement les gaussiennes visibles pendant le recouvrement (α·γ ≥ 1/255 a un moment de la zone, comme
    // l'encodeur ITHACA le calcule) ; autres formats : copie entiere. Formats par morceaux (qualites High, Medium) :
    // selection par morceaux entiers de 256 gaussiennes, le dernier morceau du cote a complete de gaussiennes sans
    // fenetre (jamais dessinees).
    // Fait sur un fil d'arriere-plan a partir des donnees preparees des deux sequences (deja recalees dans la video).
    public static class GaussianSplatJunction
    {
        // fenetres des cotes (temps video normalise), comme les autres fenetres : [debut, fin[
        public struct Zone
        {
            public float uStart, uEnd;           // zone de recouvrement (centres des images extremes ± une demi-image)
            public float winA0, winA1;           // fenetre du cote a
            public float winB0, winB1;           // fenetre du cote a + 1
        }

        public static bool Compatible(GaussianSplatRuntimeLoader.Prepared a, GaussianSplatRuntimeLoader.Prepared b) =>
            a != null && b != null && a.temporal != null && b.temporal != null &&
            a.temporalFormat == b.temporalFormat &&
            (a.temporalFormat == GaussianSplatAsset.TemporalFormat.FreeTimeGS || a.temporalFormat == GaussianSplatAsset.TemporalFormat.Trigs) &&
            a.formatPos == b.formatPos && a.formatScale == b.formatScale && a.formatColor == b.formatColor && a.formatSH == b.formatSH &&
            (a.chunk == null) == (b.chunk == null) && a.formatColor != GaussianSplatAsset.ColorFormat.BC7 &&
            (int)a.formatSH <= (int)GaussianSplatAsset.SHFormat.Norm6;

        // indices des gaussiennes gardees (-1 : bourrage), multiple de 256 si pad
        static List<int> Selection(GaussianSplatRuntimeLoader.Prepared p, Zone z, bool pad)
        {
            int n = p.splatCount, chunk = GaussianSplatAsset.kChunkSize;
            var keep = new List<int>(n);
            bool trigs = p.temporalFormat == GaussianSplatAsset.TemporalFormat.Trigs;
            bool chunked = p.chunk != null;
            if (!trigs)
            {
                for (int i = 0; i < n; i++) keep.Add(i);
            }
            else
            {
                // opacite de base : lue dans la texture des couleurs quand elle est en flottants 32 bits ; sinon 1
                // (prudent : garde davantage)
                byte[] col = p.formatColor == GaussianSplatAsset.ColorFormat.Float32x4 && !chunked ? p.color : null;
                bool Vivante(int i) => Visible(p.temporal, col, i, z);
                if (!chunked)
                {
                    for (int i = 0; i < n; i++) if (Vivante(i)) keep.Add(i);
                }
                else
                    for (int c0 = 0; c0 < n; c0 += chunk)
                    {
                        int fin = Math.Min(n, c0 + chunk);
                        bool k = false;
                        for (int i = c0; i < fin && !k; i++) k = Vivante(i);
                        if (k) for (int i = c0; i < fin; i++) keep.Add(i);
                    }
            }
            if (pad && chunked)
                while (keep.Count % chunk != 0) keep.Add(-1);
            return keep;
        }

        // HD 4DGS : la gaussienne i est-elle visible pendant la zone (opacite x temporelle >= 1/255 a un moment) ?
        static bool Visible(byte[] temporal, byte[] color, int i, Zone z)
        {
            var t = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(new ReadOnlySpan<byte>(temporal));
            int r = i * (GaussianSplatAsset.kTemporalStrideTrigs / 4);
            float mu = t[r], sig = t[r + 1], tScale = t[r + 25], c = t[r + 26], u0 = t[r + 27];
            float opa = 1f;
            if (color != null)
                opa = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(new ReadOnlySpan<byte>(color))[GaussianSplatAssetBuilder.SplatIndexToTextureIndex((uint)i) * 4 + 3];
            double thr = c + (1.0 - c) / (255.0 * Math.Max(opa, 1e-12));
            if (thr >= 1) return false;
            double h = Math.Sqrt(-2.0 * Math.Log(Math.Max(thr, 1e-12))) * sig, ts = Math.Max(tScale, 1e-12);
            return u0 + (mu + h) / ts >= z.uStart && u0 + (mu - h) / ts <= z.uEnd;
        }

        static int NextMultipleOf(int size, int m) => (size + m - 1) / m * m;

        // Modele de jonction (null si les deux sequences ne se fondent pas : formats differents)
        public static GaussianSplatRuntimeLoader.Prepared Merge(GaussianSplatRuntimeLoader.Prepared A, GaussianSplatRuntimeLoader.Prepared B, Zone z, out int split)
        {
            split = 0;
            if (!Compatible(A, B)) return null;
            bool chunked = A.chunk != null;
            var selA = Selection(A, z, true);
            var selB = Selection(B, z, false);
            int nA = selA.Count, n = nA + selB.Count;
            if (n == 0) return null;
            split = nA;

            int posSz = GaussianSplatAsset.GetVectorSize(A.formatPos), othSz = GaussianSplatAsset.GetOtherSizeNoSHIndex(A.formatScale);
            int shSz = (int)GaussianSplatAsset.CalcSHDataSize(1, A.formatSH), colSz = GaussianSplatAsset.GetColorSize(A.formatColor);
            bool trigs = A.temporalFormat == GaussianSplatAsset.TemporalFormat.Trigs;
            int tSz = trigs ? GaussianSplatAsset.kTemporalStrideTrigs : GaussianSplatAsset.kTemporalStrideFTGS, wOff = trigs ? 92 : 20;
            var (tw, th) = GaussianSplatAsset.CalcTextureSize(n);

            var r = new GaussianSplatRuntimeLoader.Prepared
            {
                name = A.name + "+" + B.name, splatCount = n,
                formatPos = A.formatPos, formatScale = A.formatScale, formatColor = A.formatColor, formatSH = A.formatSH,
                boundsMin = Vector3.Min(A.boundsMin, B.boundsMin), boundsMax = Vector3.Max(A.boundsMax, B.boundsMax),
                robustMin = A.robustMin, robustMax = A.robustMax,
                temporalFormat = A.temporalFormat, temporalFrames = A.temporalFrames, temporalFirstFrame = A.temporalFirstFrame,
                temporalSegments = A.temporalSegments, isTemporal = true,
                pos = new byte[NextMultipleOf(n * posSz, 8)],
                other = new byte[NextMultipleOf(n * othSz, 8)],
                sh = new byte[(long)n * shSz],
                color = new byte[(long)tw * th * colSz],
                temporal = new byte[(long)n * tSz],
                playStart = Math.Min(z.winA0, z.winB0), playEnd = Math.Max(z.winA1, z.winB1),
            };
            var hash = new Hash128((uint)n, (uint)split, 0x4a4e4354u, 0);
            hash.Append(A.dataHash.ToString()); hash.Append(B.dataHash.ToString());
            r.dataHash = hash;

            var wA0 = BitConverter.GetBytes(z.winA0); var wA1 = BitConverter.GetBytes(z.winA1);
            var wB0 = BitConverter.GetBytes(z.winB0); var wB1 = BitConverter.GetBytes(z.winB1);
            var vide0 = BitConverter.GetBytes(2f); var vide1 = BitConverter.GetBytes(-1f);
            for (int d = 0; d < n; d++)
            {
                bool cote1 = d < nA;
                var src = cote1 ? A : B;
                int s = cote1 ? selA[d] : selB[d - nA];
                int dt = d * tSz;
                if (s < 0)
                {
                    // bourrage : fenetre vide, jamais dessinee
                    Buffer.BlockCopy(vide0, 0, r.temporal, dt + wOff, 4);
                    Buffer.BlockCopy(vide1, 0, r.temporal, dt + wOff + 4, 4);
                    continue;
                }
                Buffer.BlockCopy(src.pos, s * posSz, r.pos, d * posSz, posSz);
                Buffer.BlockCopy(src.other, s * othSz, r.other, d * othSz, othSz);
                Buffer.BlockCopy(src.sh, s * shSz, r.sh, d * shSz, shSz);
                int ts = GaussianSplatAssetBuilder.SplatIndexToTextureIndex((uint)s), td = GaussianSplatAssetBuilder.SplatIndexToTextureIndex((uint)d);
                Buffer.BlockCopy(src.color, ts * colSz, r.color, td * colSz, colSz);
                Buffer.BlockCopy(src.temporal, s * tSz, r.temporal, dt, tSz);
                Buffer.BlockCopy(cote1 ? wA0 : wB0, 0, r.temporal, dt + wOff, 4);
                Buffer.BlockCopy(cote1 ? wA1 : wB1, 0, r.temporal, dt + wOff + 4, 4);
            }
            if (chunked)
            {
                // morceaux entiers : ceux des gaussiennes gardees, dans l'ordre
                int cs = System.Runtime.InteropServices.Marshal.SizeOf<GaussianSplatAsset.ChunkInfo>(), k = GaussianSplatAsset.kChunkSize;
                int nc = (n + k - 1) / k;
                r.chunk = new byte[(long)nc * cs];
                for (int c = 0; c < nc; c++)
                {
                    int d = c * k;
                    bool cote1 = d < nA;
                    int s = cote1 ? selA[d] : selB[d - nA];
                    if (s < 0) continue;     // morceau de bourrage seul : impossible (bourrage en fin de morceau)
                    Buffer.BlockCopy((cote1 ? A : B).chunk, s / k * cs, r.chunk, c * cs, cs);
                }
            }
            return r;
        }
    }
}
