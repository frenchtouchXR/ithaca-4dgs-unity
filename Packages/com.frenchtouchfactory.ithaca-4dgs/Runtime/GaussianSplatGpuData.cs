// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GaussianSplatting4D.Runtime
{
    // Donnees GPU d'un modele prepare pendant l'execution (GaussianSplatRuntimeLoader.Prepared), envoyees par morceaux
    // (24/11/2026, comme le lecteur JS 0.4.0) : les buffers et la texture des couleurs sont seulement reserves, puis
    // remplis par tranches au fil des images dans un budget d'octets par image (Upload), au lieu d'un envoi entier
    // (jusqu'a ≈ 300 Mo pour un million de gaussiennes) dans une seule image a l'activation de la sequence.
    // La texture des couleurs est remplie par bandes de lignes (petite texture intermediaire copiee sur le GPU).
    // Une fois complet, le modele est adopte tel quel par Gaussian4DSplatRenderer (aucune copie) et le buffer temporel
    // par Gaussian4DPlayer ; il appartient a l'asset (GaussianSplatAsset.gpuData) et est libere avec lui.
    public sealed class GaussianSplatGpuData : IDisposable
    {
        public GraphicsBuffer pos, other, sh, chunks, temporal;
        public Texture color;
        public bool chunksValid;
        public int splatCount;
        // plage de lecture de la fenetre des sequences (FreeTimeGS, HD 4DGS), calculee en arriere-plan ; -1 : aucune
        public float playStart, playEnd = -1f;
        public long totalBytes { get; private set; }
        public long remainingBytes { get; private set; }
        public bool complete => remainingBytes == 0 && m_Jobs.Count == 0;

        class Job
        {
            public GraphicsBuffer buffer;     // ou texture (color)
            public byte[] data;
            public int stride;               // octets par element du buffer
            public long done;
            public int rowBytes, rows;       // texture : octets par ligne, lignes
            public int width;
            public GraphicsFormat format;
        }

        readonly List<Job> m_Jobs = new();

        // ressources appartenant a un modele envoye par morceaux : jamais liberees par le rendu qui les adopte
        static readonly HashSet<UnityEngine.Object> s_Textures = new();
        static readonly HashSet<GraphicsBuffer> s_Buffers = new();
        public static bool Owns(GraphicsBuffer b) => b != null && s_Buffers.Contains(b);
        public static bool Owns(Texture t) => t != null && s_Textures.Contains(t);
        // modeles necessaires a l'affichage avant d'etre entierement envoyes, finis d'un coup (doit rester 0)
        public static int forcedUploads;

        // Fil principal : reservation sur le GPU (sans donnees). Les tableaux de p sont lus pendant les envois : ne pas
        // les modifier avant complete.
        public static GaussianSplatGpuData Create(GaussianSplatRuntimeLoader.Prepared p)
        {
            var g = new GaussianSplatGpuData { splatCount = p.splatCount };
            g.pos = g.Buffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, p.pos, 4, "GaussianPosData");
            g.other = g.Buffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, p.other, 4, "GaussianOtherData");
            g.sh = g.Buffer(GraphicsBuffer.Target.Raw, p.sh, 4, "GaussianSHData");
            int chunkSize = UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>();
            if (p.chunk != null && p.chunk.Length != 0)
            {
                g.chunks = g.Buffer(GraphicsBuffer.Target.Structured, p.chunk, chunkSize, "GaussianChunkData");
                g.chunksValid = true;
            }
            else
                g.chunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, chunkSize) { name = "GaussianChunkData" };
            if (p.temporal != null && p.temporal.Length >= 4)
                g.temporal = g.Buffer(GraphicsBuffer.Target.Raw, p.temporal, 4, "Gaussian4DTemporalData");
            // couleurs : texture de rendu (aucune copie cote processeur), remplie par bandes
            var (w, h) = GaussianSplatAsset.CalcTextureSize(p.splatCount);
            var fmt = GaussianSplatAsset.ColorFormatToGraphics(p.formatColor);
            bool bandes = SystemInfo.copyTextureSupport.HasFlag(CopyTextureSupport.TextureToRT) && p.formatColor != GaussianSplatAsset.ColorFormat.BC7
                          && SystemInfo.IsFormatSupported(fmt, GraphicsFormatUsage.Render);
            if (bandes)
            {
                var rt = new RenderTexture(new RenderTextureDescriptor(w, h, fmt, GraphicsFormat.None) { msaaSamples = 1, mipCount = 1, useMipMap = false, autoGenerateMips = false })
                    { name = "GaussianColorData", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                rt.Create();
                g.color = rt;
                g.m_Jobs.Add(new Job { data = p.color, rowBytes = p.color.Length / h, rows = h, width = w, format = fmt });
                g.totalBytes += p.color.Length;
            }
            else
            {
                // repli : texture ordinaire envoyee entiere (comme Gaussian4DSplatRenderer)
                var tex = new Texture2D(w, h, fmt, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
                tex.SetPixelData(p.color, 0);
                tex.Apply(false, true);
                g.color = tex;
            }
            g.remainingBytes = g.totalBytes;
            foreach (var b in new[] { g.pos, g.other, g.sh, g.chunks, g.temporal }) if (b != null) s_Buffers.Add(b);
            s_Textures.Add(g.color);
            // ordre des envois : couleurs, positions et le reste (tout est necessaire au premier dessin)
            return g;
        }

        GraphicsBuffer Buffer(GraphicsBuffer.Target target, byte[] data, int stride, string name)
        {
            var b = new GraphicsBuffer(target, Math.Max(1, data.Length / stride), stride) { name = name };
            m_Jobs.Add(new Job { buffer = b, data = data, stride = stride });
            totalBytes += data.Length;
            return b;
        }

        // Fil principal : envoie au plus ≈ maxBytes (au moins un morceau) ; renvoie les octets envoyes
        public long Upload(long maxBytes)
        {
            long sent = 0;
            while (m_Jobs.Count > 0 && (sent < maxBytes || sent == 0))
            {
                var j = m_Jobs[0];
                long budget = Math.Max(1, maxBytes - sent);
                if (j.buffer == null)
                {
                    // texture : bandes de lignes (multiple de 16 pour rester aligne sur les tuiles), au moins une
                    int row = (int)(j.done / j.rowBytes);
                    int n = (int)Math.Clamp(budget / j.rowBytes, 1, j.rows - row);
                    var bande = new Texture2D(j.width, n, j.format, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorBand" };
                    bande.SetPixelData(j.data, 0, (int)j.done);
                    bande.Apply(false, true);
                    Graphics.CopyTexture(bande, 0, 0, 0, 0, j.width, n, color, 0, 0, 0, row);
                    Detruire(bande);
                    long b = (long)n * j.rowBytes;
                    j.done += b; sent += b;
                    if (row + n >= j.rows) m_Jobs.RemoveAt(0);
                }
                else
                {
                    long left = j.data.Length - j.done;
                    long b = Math.Min(left, Math.Max(budget / j.stride, 1) * j.stride);
                    SetData(j.buffer, j.data, (int)(j.done / j.stride), (int)(b / j.stride), j.stride);
                    j.done += b; sent += b;
                    if (j.done >= j.data.Length) m_Jobs.RemoveAt(0);
                }
            }
            remainingBytes = Math.Max(0, remainingBytes - sent);
            if (m_Jobs.Count == 0) remainingBytes = 0;
            return sent;
        }

        // elements [first, first + count) du tableau d'octets vers le buffer (meme pas), sans copie intermediaire
        static unsafe void SetData(GraphicsBuffer buf, byte[] data, int first, int count, int stride)
        {
            if (count <= 0) return;
            fixed (byte* p = data)
            {
                if (stride == 4)
                {
                    var view = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<uint>(p + (long)first * 4, count, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    var h = AtomicSafetyHandle.Create();
                    NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref view, h);
#endif
                    buf.SetData(view, 0, first, count);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    AtomicSafetyHandle.Release(h);
#endif
                }
                else
                {
                    var view = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<GaussianSplatAsset.ChunkInfo>(p + (long)first * stride, count, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    var h = AtomicSafetyHandle.Create();
                    NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref view, h);
#endif
                    buf.SetData(view, 0, first, count);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    AtomicSafetyHandle.Release(h);
#endif
                }
            }
        }

        static void Detruire(UnityEngine.Object o)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }

        public void Dispose()
        {
            foreach (var b in new[] { pos, other, sh, chunks, temporal }) if (b != null) { s_Buffers.Remove(b); b.Dispose(); }
            pos = other = sh = chunks = temporal = null;
            if (color != null)
            {
                s_Textures.Remove(color);
                if (color is RenderTexture rt) rt.Release();
                Detruire(color);
                color = null;
            }
            m_Jobs.Clear();
            remainingBytes = 0;
        }
    }
}
