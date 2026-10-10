using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GaussianSplatting4D.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;

namespace GaussianSplatting4D.Editor.Utils
{
    // input file splat data is read into this format
    public struct InputSplatData
    {
        public Vector3 pos;
        public Vector3 nor;
        public Vector3 dc0;
        public Vector3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
        public float opacity;
        public Vector3 scale;
        public Quaternion rot;
        // 4D temporal
        public float t;
        public float scale_t;
        public Quaternion rot_r;
        // raw quaternion saved before LinearizeData (4 extra floats, mapped to dummy PLY attrs)
        public Quaternion rot_raw_val;
        // FreeTimeGS (.ftgs.ply) : temps normalise, log de la duree de vie, vitesse, fenetre de sequence
        public float ftgs_time;
        public float ftgs_log_duration;
        public Vector3 ftgs_velocity;
        public float ftgs_seg_start, ftgs_seg_end;
    }

    // Variante temporelle d'un fichier d'entree, d'apres ses proprietes PLY
    public enum TemporalKind
    {
        None,       // 3DGS statique
        FourC4D,    // 4C4D / 4DGS : t, scale_t, rot_r_*
        FreeTimeGS  // FreeTimeGS v1 : time, log_duration, velocity_* (+ seg_start/seg_end ITHACA)
    }

    // Informations FreeTimeGS lues dans l'en-tete (lignes comment)
    public struct FtgsHeaderInfo
    {
        public int nFrames;          // nombre de frames couvertes par le temps normalise [0,1]
        public float minDuration;    // duree de vie minimale (temps normalise)
        public bool useVelocity;
        public bool hasWindow;       // proprietes seg_start/seg_end presentes (assemble_ftgs.py)
        public int firstFrame;       // ithaca_first_frame
        public int segments;         // ithaca_segments
    }

    [BurstCompile]
    public class GaussianFileReader
    {
        // Returns splat count
        public static int ReadFileHeader(string filePath)
        {
            int vertexCount = 0;
            if (File.Exists(filePath))
            {
                if (isPLY(filePath))
                    PLYFileReader.ReadFileHeader(filePath, out vertexCount, out _, out _);
                else if (isSPZ(filePath))
                    SPZFileReader.ReadFileHeader(filePath, out vertexCount);
            }
            return vertexCount;
        }

        // Type temporel d'apres l'en-tete seul (affichage dans la fenetre de creation d'asset)
        public static TemporalKind ReadTemporalKind(string filePath)
        {
            if (!File.Exists(filePath) || !isPLY(filePath))
                return TemporalKind.None;
            PLYFileReader.ReadFileHeader(filePath, out _, out _, out var attrs);
            return DetectTemporalKind(attrs);
        }

        public static void ReadFile(string filePath, out NativeArray<InputSplatData> splats)
        {
            ReadFile(filePath, out splats, out _, out _);
        }

        public static unsafe void ReadFile(string filePath, out NativeArray<InputSplatData> splats, out TemporalKind temporalKind, out FtgsHeaderInfo ftgsInfo)
        {
            temporalKind = TemporalKind.None;
            ftgsInfo = default;
            if (isPLY(filePath))
            {
                NativeArray<byte> plyRawData;
                List<(string, PLYFileReader.ElementType)> attributes;
                PLYFileReader.ReadFile(filePath, out var splatCount, out var vertexStride, out attributes, out var comments, out plyRawData);
                temporalKind = DetectTemporalKind(attributes);
                if (temporalKind == TemporalKind.FreeTimeGS)
                    ftgsInfo = ParseFtgsHeader(filePath, comments, attributes);
                string attrError = CheckPLYAttributes(attributes);
                if (!string.IsNullOrEmpty(attrError))
                    throw new IOException($"PLY file is probably not a Gaussian Splat file? Missing properties: {attrError}");
                splats = PLYDataToSplats(plyRawData, splatCount, vertexStride, attributes);
                ReorderSHs(splatCount, (float*)splats.GetUnsafePtr());
                LinearizeData(splats);
                return;
            }
            if (isSPZ(filePath))
            {
                SPZFileReader.ReadFile(filePath, out splats);
                return;
            }
            throw new IOException($"File {filePath} is not a supported format");
        }

        static bool isPLY(string filePath) => filePath.EndsWith(".ply", true, CultureInfo.InvariantCulture);
        static bool isSPZ(string filePath) => filePath.EndsWith(".spz", true, CultureInfo.InvariantCulture);

        static bool HasFloat(List<(string, PLYFileReader.ElementType)> attributes, string name) =>
            attributes.Contains((name, PLYFileReader.ElementType.Float));

        static TemporalKind DetectTemporalKind(List<(string, PLYFileReader.ElementType)> attributes)
        {
            if (HasFloat(attributes, "time") && HasFloat(attributes, "log_duration") && HasFloat(attributes, "velocity_0"))
                return TemporalKind.FreeTimeGS;
            if (HasFloat(attributes, "t") && HasFloat(attributes, "scale_t"))
                return TemporalKind.FourC4D;
            return TemporalKind.None;
        }

        static FtgsHeaderInfo ParseFtgsHeader(string filePath, Dictionary<string, string> comments, List<(string, PLYFileReader.ElementType)> attributes)
        {
            string Get(string key, string def) => comments.TryGetValue(key, out var v) ? v : def;
            if (Get("time_units", "normalized") != "normalized")
                throw new IOException($"PLY {filePath} : FreeTimeGS en temps '{Get("time_units", "")}' non pris en charge (temps normalise attendu)");
            var inv = CultureInfo.InvariantCulture;
            return new FtgsHeaderInfo
            {
                nFrames = int.Parse(Get("n_frames", "2"), inv),
                minDuration = float.Parse(Get("min_duration", "0"), inv),
                useVelocity = Get("use_velocity", "1") != "0",
                hasWindow = HasFloat(attributes, "seg_start") && HasFloat(attributes, "seg_end"),
                firstFrame = int.Parse(Get("ithaca_first_frame", "0"), inv),
                segments = int.Parse(Get("ithaca_segments", "1"), inv),
            };
        }

        static string CheckPLYAttributes(List<(string, PLYFileReader.ElementType)> attributes)
        {
            string[] required = { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" };
            List<string> missing = required.Where(req => !attributes.Contains((req, PLYFileReader.ElementType.Float))).ToList();
            if (missing.Count == 0)
                return null;
            return string.Join(",", missing);
        }

        static unsafe NativeArray<InputSplatData> PLYDataToSplats(NativeArray<byte> input, int count, int stride, List<(string, PLYFileReader.ElementType)> attributes)
        {
            NativeArray<int> fileAttrOffsets = new NativeArray<int>(attributes.Count, Allocator.Temp);
            int offset = 0;
            for (var ai = 0; ai < attributes.Count; ai++)
            {
                var attr = attributes[ai];
                fileAttrOffsets[ai] = offset;
                offset += PLYFileReader.TypeToSize(attr.Item2);
            }

            string[] splatAttributes =
            {
                "x",
                "y",
                "z",
                "nx",
                "ny",
                "nz",
                "f_dc_0",
                "f_dc_1",
                "f_dc_2",
                "f_rest_0",
                "f_rest_1",
                "f_rest_2",
                "f_rest_3",
                "f_rest_4",
                "f_rest_5",
                "f_rest_6",
                "f_rest_7",
                "f_rest_8",
                "f_rest_9",
                "f_rest_10",
                "f_rest_11",
                "f_rest_12",
                "f_rest_13",
                "f_rest_14",
                "f_rest_15",
                "f_rest_16",
                "f_rest_17",
                "f_rest_18",
                "f_rest_19",
                "f_rest_20",
                "f_rest_21",
                "f_rest_22",
                "f_rest_23",
                "f_rest_24",
                "f_rest_25",
                "f_rest_26",
                "f_rest_27",
                "f_rest_28",
                "f_rest_29",
                "f_rest_30",
                "f_rest_31",
                "f_rest_32",
                "f_rest_33",
                "f_rest_34",
                "f_rest_35",
                "f_rest_36",
                "f_rest_37",
                "f_rest_38",
                "f_rest_39",
                "f_rest_40",
                "f_rest_41",
                "f_rest_42",
                "f_rest_43",
                "f_rest_44",
                "opacity",
                "scale_0",
                "scale_1",
                "scale_2",
                "rot_0",
                "rot_1",
                "rot_2",
                "rot_3",
                "t",
                "scale_t",
                "rot_r_0",
                "rot_r_1",
                "rot_r_2",
                "rot_r_3"
            ,
                "rot_0", "rot_1", "rot_2", "rot_3",
                "time", "log_duration", "velocity_0", "velocity_1", "velocity_2", "seg_start", "seg_end"
            };
            // Couleurs de degre < 3 (ex. FreeTimeGS assemble en degre 1 : f_rest_0..8) : le fichier range
            // les coefficients canal par canal (k par canal) ; on les replace aux emplacements du degre 3
            // (15 par canal) attendus par ReorderSHs, les coefficients absents restent a zero.
            int restCount = 0;
            while (HasFloat(attributes, $"f_rest_{restCount}"))
                ++restCount;
            int restPerChannel = restCount / 3;
            const int kRestStart = 9, kRestPerChannel = 15;
            Assert.AreEqual(UnsafeUtility.SizeOf<InputSplatData>() / 4, splatAttributes.Length);
            NativeArray<int> srcOffsets = new NativeArray<int>(splatAttributes.Length, Allocator.Temp);
            for (int ai = 0; ai < splatAttributes.Length; ai++)
            {
                string attrName = splatAttributes[ai];
                if (restPerChannel != kRestPerChannel && ai >= kRestStart && ai < kRestStart + 3 * kRestPerChannel)
                {
                    int c = (ai - kRestStart) / kRestPerChannel, j = (ai - kRestStart) % kRestPerChannel;
                    attrName = j < restPerChannel ? $"f_rest_{c * restPerChannel + j}" : null;
                }
                int attrIndex = attrName != null ? attributes.IndexOf((attrName, PLYFileReader.ElementType.Float)) : -1;
                int attrOffset = attrIndex >= 0 ? fileAttrOffsets[attrIndex] : -1;
                srcOffsets[ai] = attrOffset;
            }
            
            NativeArray<InputSplatData> dst = new NativeArray<InputSplatData>(count, Allocator.Persistent);
            ReorderPLYData(count, (byte*)input.GetUnsafeReadOnlyPtr(), stride, (byte*)dst.GetUnsafePtr(), UnsafeUtility.SizeOf<InputSplatData>(), (int*)srcOffsets.GetUnsafeReadOnlyPtr());
            return dst;
        }

        [BurstCompile]
        static unsafe void ReorderPLYData(int splatCount, byte* src, int srcStride, byte* dst, int dstStride, int* srcOffsets)
        {
            for (int i = 0; i < splatCount; i++)
            {
                for (int attr = 0; attr < dstStride / 4; attr++)
                {
                    if (srcOffsets[attr] >= 0)
                        *(int*)(dst + attr * 4) = *(int*)(src + srcOffsets[attr]);
                }
                src += srcStride;
                dst += dstStride;
            }
        }

        [BurstCompile]
        static unsafe void ReorderSHs(int splatCount, float* data)
        {
            int splatStride = UnsafeUtility.SizeOf<InputSplatData>() / 4;
            int shStartOffset = 9, shCount = 15;
            float* tmp = stackalloc float[shCount * 3];
            int idx = shStartOffset;
            for (int i = 0; i < splatCount; ++i)
            {
                for (int j = 0; j < shCount; ++j)
                {
                    tmp[j * 3 + 0] = data[idx + j];
                    tmp[j * 3 + 1] = data[idx + j + shCount];
                    tmp[j * 3 + 2] = data[idx + j + shCount * 2];
                }

                for (int j = 0; j < shCount * 3; ++j)
                {
                    data[idx + j] = tmp[j];
                }

                idx += splatStride;
            }
        }

        [BurstCompile]
        struct LinearizeDataJob : IJobParallelFor
        {
            public NativeArray<InputSplatData> splatData;
            public void Execute(int index)
            {
                var splat = splatData[index];

                // rot
                var q = splat.rot;
                splat.rot_raw_val = splat.rot;  // save raw before compression
                var qq = GaussianUtils.NormalizeSwizzleRotation(new float4(q.x, q.y, q.z, q.w));
                qq = GaussianUtils.PackSmallest3Rotation(qq);
                splat.rot = new Quaternion(qq.x, qq.y, qq.z, qq.w);

                // scale
                splat.scale = GaussianUtils.LinearScale(splat.scale);

                // color
                splat.dc0 = GaussianUtils.SH0ToColor(splat.dc0);
                splat.opacity = GaussianUtils.Sigmoid(splat.opacity);

                splatData[index] = splat;
            }
        }

        static void LinearizeData(NativeArray<InputSplatData> splatData)
        {
            LinearizeDataJob job = new LinearizeDataJob();
            job.splatData = splatData;
            job.Schedule(splatData.Length, 4096).Complete();
        }
    }
}