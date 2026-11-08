// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting4D.Editor.Utils;
using GaussianSplatting4D.Runtime.Utils;
using GaussianSplatting4D.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GaussianSplatting4D.Editor
{
    [BurstCompile]
    public class GaussianSplatAssetCreator : EditorWindow
    {
        const string kProgressTitle = "Creating Gaussian Splat Asset";
        const string kCamerasJson = "cameras.json";
        const string kPrefQuality = "nesnausk.GaussianSplatting.CreatorQuality";
        const string kPrefOutputFolder = "nesnausk.GaussianSplatting.CreatorOutputFolder";

        enum DataQuality
        {
            VeryHigh,
            High,
            Medium,
            Low,
            VeryLow,
            Custom,
        }

        readonly FilePickerControl m_FilePicker = new();

        [SerializeField] string m_InputFile;
        [SerializeField] bool m_ImportCameras = true;

        [SerializeField] string m_OutputFolder = "Assets/GaussianAssets";
        [SerializeField] DataQuality m_Quality = DataQuality.Medium;
        [SerializeField] GaussianSplatAsset.VectorFormat m_FormatPos;
        [SerializeField] GaussianSplatAsset.VectorFormat m_FormatScale;
        [SerializeField] GaussianSplatAsset.ColorFormat m_FormatColor;
        [SerializeField] GaussianSplatAsset.SHFormat m_FormatSH;

        string m_ErrorMessage;
        string m_PrevFilePath;
        int m_PrevVertexCount;
        long m_PrevFileSize;
        TemporalKind m_PrevTemporalKind;
        // renseignes par LoadInputSplatFile
        TemporalKind m_TemporalKind;
        FtgsHeaderInfo m_FtgsInfo;

        bool isUsingChunks => GaussianSplatAssetBuilder.UsesChunks(m_FormatPos, m_FormatScale, m_FormatColor, m_FormatSH);

        [MenuItem("Tools/Gaussian Splats/Create GaussianSplatAsset")]
        public static void Init()
        {
            var window = GetWindowWithRect<GaussianSplatAssetCreator>(new Rect(50, 50, 360, 340), false, "Gaussian Splat Creator", true);
            window.minSize = new Vector2(320, 320);
            window.maxSize = new Vector2(1500, 1500);
            window.Show();
        }

        void Awake()
        {
            m_Quality = (DataQuality)EditorPrefs.GetInt(kPrefQuality, (int)DataQuality.Medium);
            m_OutputFolder = EditorPrefs.GetString(kPrefOutputFolder, "Assets/GaussianAssets");
        }

        void OnEnable()
        {
            ApplyQualityLevel();
        }

        void OnGUI()
        {
            EditorGUILayout.Space();
            GUILayout.Label("Input data", EditorStyles.boldLabel);
            var rect = EditorGUILayout.GetControlRect(true);
            m_InputFile = m_FilePicker.PathFieldGUI(rect, new GUIContent("Input PLY/SPZ/FTGSZ File"), m_InputFile, "ply,spz,ftgsz", "PointCloudFile");
            m_ImportCameras = EditorGUILayout.Toggle("Import Cameras", m_ImportCameras);

            if (m_InputFile != m_PrevFilePath && !string.IsNullOrWhiteSpace(m_InputFile))
            {
                m_PrevVertexCount = 0;
                m_ErrorMessage = null;
                try
                {
                    m_PrevVertexCount = GaussianFileReader.ReadFileHeader(m_InputFile);
                    m_PrevTemporalKind = GaussianFileReader.ReadTemporalKind(m_InputFile);
                }
                catch (Exception ex)
                {
                    m_ErrorMessage = ex.Message;
                }

                m_PrevFileSize = File.Exists(m_InputFile) ? new FileInfo(m_InputFile).Length : 0;
                m_PrevFilePath = m_InputFile;
            }

            if (m_PrevVertexCount > 0)
            {
                EditorGUILayout.LabelField("File Size", $"{EditorUtility.FormatBytes(m_PrevFileSize)} - {m_PrevVertexCount:N0} splats");
                EditorGUILayout.LabelField("Type", m_PrevTemporalKind switch
                {
                    TemporalKind.FreeTimeGS => "4D FreeTimeGS (.ftgs.ply)",
                    TemporalKind.FourC4D => "4D 4C4D",
                    _ => "3DGS (static)"
                });
            }
            else
                GUILayout.Space(EditorGUIUtility.singleLineHeight);

            EditorGUILayout.Space();
            GUILayout.Label("Output", EditorStyles.boldLabel);
            rect = EditorGUILayout.GetControlRect(true);
            string newOutputFolder = m_FilePicker.PathFieldGUI(rect, new GUIContent("Output Folder"), m_OutputFolder, null, "GaussianAssetOutputFolder");
            if (newOutputFolder != m_OutputFolder)
            {
                m_OutputFolder = newOutputFolder;
                EditorPrefs.SetString(kPrefOutputFolder, m_OutputFolder);
            }

            var newQuality = (DataQuality) EditorGUILayout.EnumPopup("Quality", m_Quality);
            if (newQuality != m_Quality)
            {
                m_Quality = newQuality;
                EditorPrefs.SetInt(kPrefQuality, (int)m_Quality);
                ApplyQualityLevel();
            }

            long sizePos = 0, sizeOther = 0, sizeCol = 0, sizeSHs = 0, totalSize = 0;
            if (m_PrevVertexCount > 0)
            {
                sizePos = GaussianSplatAsset.CalcPosDataSize(m_PrevVertexCount, m_FormatPos);
                sizeOther = GaussianSplatAsset.CalcOtherDataSize(m_PrevVertexCount, m_FormatScale);
                sizeCol = GaussianSplatAsset.CalcColorDataSize(m_PrevVertexCount, m_FormatColor);
                sizeSHs = GaussianSplatAsset.CalcSHDataSize(m_PrevVertexCount, m_FormatSH);
                long sizeChunk = isUsingChunks ? GaussianSplatAsset.CalcChunkDataSize(m_PrevVertexCount) : 0;
                totalSize = sizePos + sizeOther + sizeCol + sizeSHs + sizeChunk;
            }

            const float kSizeColWidth = 70;
            EditorGUI.BeginDisabledGroup(m_Quality != DataQuality.Custom);
            EditorGUI.indentLevel++;
            GUILayout.BeginHorizontal();
            m_FormatPos = (GaussianSplatAsset.VectorFormat)EditorGUILayout.EnumPopup("Position", m_FormatPos);
            GUILayout.Label(sizePos > 0 ? EditorUtility.FormatBytes(sizePos) : string.Empty, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            m_FormatScale = (GaussianSplatAsset.VectorFormat)EditorGUILayout.EnumPopup("Scale", m_FormatScale);
            GUILayout.Label(sizeOther > 0 ? EditorUtility.FormatBytes(sizeOther) : string.Empty, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            m_FormatColor = (GaussianSplatAsset.ColorFormat)EditorGUILayout.EnumPopup("Color", m_FormatColor);
            GUILayout.Label(sizeCol > 0 ? EditorUtility.FormatBytes(sizeCol) : string.Empty, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            m_FormatSH = (GaussianSplatAsset.SHFormat) EditorGUILayout.EnumPopup("SH", m_FormatSH);
            GUIContent shGC = new GUIContent();
            shGC.text = sizeSHs > 0 ? EditorUtility.FormatBytes(sizeSHs) : string.Empty;
            if (m_FormatSH >= GaussianSplatAsset.SHFormat.Cluster64k)
            {
                shGC.tooltip = "Note that SH clustering is not fast! (3-10 minutes for 6M splats)";
                shGC.image = EditorGUIUtility.IconContent("console.warnicon.sml").image;
            }
            GUILayout.Label(shGC, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            EditorGUI.indentLevel--;
            EditorGUI.EndDisabledGroup();
            if (totalSize > 0)
                EditorGUILayout.LabelField("Asset Size", $"{EditorUtility.FormatBytes(totalSize)} - {(double) m_PrevFileSize / totalSize:F2}x smaller");
            else
                GUILayout.Space(EditorGUIUtility.singleLineHeight);


            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            GUILayout.Space(30);
            if (GUILayout.Button("Create Asset"))
            {
                CreateAsset();
            }
            GUILayout.Space(30);
            GUILayout.EndHorizontal();

            if (!string.IsNullOrWhiteSpace(m_ErrorMessage))
            {
                EditorGUILayout.HelpBox(m_ErrorMessage, MessageType.Error);
            }
        }

        void ApplyQualityLevel()
        {
            switch (m_Quality)
            {
                case DataQuality.Custom:
                    break;
                case DataQuality.VeryLow: // 18.62x smaller, 32.27 PSNR
                    m_FormatPos = GaussianSplatAsset.VectorFormat.Norm11;
                    m_FormatScale = GaussianSplatAsset.VectorFormat.Norm6;
                    m_FormatColor = GaussianSplatAsset.ColorFormat.BC7;
                    m_FormatSH = GaussianSplatAsset.SHFormat.Cluster4k;
                    break;
                case DataQuality.Low: // 14.01x smaller, 35.17 PSNR
                    m_FormatPos = GaussianSplatAsset.VectorFormat.Norm11;
                    m_FormatScale = GaussianSplatAsset.VectorFormat.Norm6;
                    m_FormatColor = GaussianSplatAsset.ColorFormat.Norm8x4;
                    m_FormatSH = GaussianSplatAsset.SHFormat.Cluster16k;
                    break;
                case DataQuality.Medium: // 5.14x smaller, 47.46 PSNR
                    m_FormatPos = GaussianSplatAsset.VectorFormat.Norm11;
                    m_FormatScale = GaussianSplatAsset.VectorFormat.Norm11;
                    m_FormatColor = GaussianSplatAsset.ColorFormat.Norm8x4;
                    m_FormatSH = GaussianSplatAsset.SHFormat.Norm6;
                    break;
                case DataQuality.High: // 2.94x smaller, 57.77 PSNR
                    m_FormatPos = GaussianSplatAsset.VectorFormat.Norm16;
                    m_FormatScale = GaussianSplatAsset.VectorFormat.Norm16;
                    m_FormatColor = GaussianSplatAsset.ColorFormat.Float16x4;
                    m_FormatSH = GaussianSplatAsset.SHFormat.Norm11;
                    break;
                case DataQuality.VeryHigh: // 1.05x smaller
                    m_FormatPos = GaussianSplatAsset.VectorFormat.Float32;
                    m_FormatScale = GaussianSplatAsset.VectorFormat.Float32;
                    m_FormatColor = GaussianSplatAsset.ColorFormat.Float32x4;
                    m_FormatSH = GaussianSplatAsset.SHFormat.Float32;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }


        static T CreateOrReplaceAsset<T>(T asset, string path) where T : UnityEngine.Object
        {
            T result = AssetDatabase.LoadAssetAtPath<T>(path);
            if (result == null)
            {
                AssetDatabase.CreateAsset(asset, path);
                result = asset;
            }
            else
            {
                if (typeof(Mesh).IsAssignableFrom(typeof(T))) { (result as Mesh)?.Clear(); }
                EditorUtility.CopySerialized(asset, result);
            }
            return result;
        }

        unsafe void CreateAsset()
        {
            m_ErrorMessage = null;
            if (string.IsNullOrWhiteSpace(m_InputFile))
            {
                m_ErrorMessage = $"Select input PLY/SPZ/FTGSZ file";
                return;
            }

            if (string.IsNullOrWhiteSpace(m_OutputFolder) || !m_OutputFolder.StartsWith("Assets/"))
            {
                m_ErrorMessage = $"Output folder must be within project, was '{m_OutputFolder}'";
                return;
            }
            Directory.CreateDirectory(m_OutputFolder);

            EditorUtility.DisplayProgressBar(kProgressTitle, "Reading data files", 0.0f);
            GaussianSplatAsset.CameraInfo[] cameras = LoadJsonCamerasFile(m_InputFile, m_ImportCameras);
            using NativeArray<InputSplatData> inputSplats = LoadInputSplatFile(m_InputFile);
            if (inputSplats.Length == 0)
            {
                EditorUtility.ClearProgressBar();
                return;
            }

            GaussianSplatAssetBuilder.CalcBounds(inputSplats, out float3 boundsMin, out float3 boundsMax);

            EditorUtility.DisplayProgressBar(kProgressTitle, "Morton reordering", 0.05f);
            GaussianSplatAssetBuilder.ReorderMorton(inputSplats, boundsMin, boundsMax);

            // cluster SHs
            NativeArray<int> splatSHIndices = default;
            NativeArray<GaussianSplatAsset.SHTableItemFloat16> clusteredSHs = default;
            if (m_FormatSH >= GaussianSplatAsset.SHFormat.Cluster64k)
            {
                EditorUtility.DisplayProgressBar(kProgressTitle, "Cluster SHs", 0.2f);
                ClusterSHs(inputSplats, m_FormatSH, out clusteredSHs, out splatSHIndices);
            }

            string baseName = Path.GetFileNameWithoutExtension(FilePickerControl.PathToDisplayString(m_InputFile));

            EditorUtility.DisplayProgressBar(kProgressTitle, "Creating data objects", 0.7f);
            GaussianSplatAsset asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            asset.Initialize(inputSplats.Length, m_FormatPos, m_FormatScale, m_FormatColor, m_FormatSH, boundsMin, boundsMax, cameras);
            asset.name = baseName;

            var dataHash = new Hash128((uint)asset.splatCount, (uint)asset.formatVersion, 0, 0);
            string pathChunk = $"{m_OutputFolder}/{baseName}_chk.bytes";
            string pathPos = $"{m_OutputFolder}/{baseName}_pos.bytes";
            string pathOther = $"{m_OutputFolder}/{baseName}_oth.bytes";
            string pathCol = $"{m_OutputFolder}/{baseName}_col.bytes";
            string pathSh = $"{m_OutputFolder}/{baseName}_shs.bytes";
            string pathTemporal = $"{m_OutputFolder}/{baseName}_tmp.bytes";

            // Le buffer temporel doit lire les donnees BRUTES (notamment scale, en log reel) --
            // CreateChunkData() ci-dessous reecrit inputSplats[i].scale EN PLACE (pow(1/8) puis
            // normalisation [0,1] par chunk), pour les presets compresses (Medium/Low/High/VeryLow).
            // Generer le buffer temporel AVANT ce remaniement, sinon Calc4DOffset (cote GPU) recoit
            // un log() d'une valeur deja transformee/normalisee au lieu de la vraie echelle, ce qui
            // fait exploser numeriquement la covariance temporelle (gaussiennes regroupees en boule
            // chaotique) -- reproductible uniquement sur les presets utilisant des chunks, jamais sur
            // VeryHigh (qui ne chunke pas, cf. isUsingChunks).
            if (m_TemporalKind == TemporalKind.FreeTimeGS)
            {
                using (var fs = new FileStream(pathTemporal, FileMode.Create, FileAccess.Write))
                    GaussianSplatAssetBuilder.CreateTemporalDataFTGS(inputSplats, fs, ref dataHash, m_FtgsInfo);
                asset.SetTemporalInfo(GaussianSplatAsset.TemporalFormat.FreeTimeGS, m_FtgsInfo.nFrames, m_FtgsInfo.firstFrame, m_FtgsInfo.segments);
                Debug.Log($"[GaussianSplatAssetCreator] FreeTimeGS : {m_FtgsInfo.nFrames} frames, {m_FtgsInfo.segments} sequence(s), " +
                          $"fenetre de sequence {(m_FtgsInfo.hasWindow ? "oui" : "non")}");
            }
            else
                using (var fs = new FileStream(pathTemporal, FileMode.Create, FileAccess.Write))
                    GaussianSplatAssetBuilder.CreateTemporalData(inputSplats, fs, ref dataHash);

            // if we are using full lossless (FP32) data, then do not use any chunking, and keep data as-is
            bool useChunks = isUsingChunks;
            if (useChunks)
                using (var fs = new FileStream(pathChunk, FileMode.Create, FileAccess.Write))
                    GaussianSplatAssetBuilder.CreateChunkData(inputSplats, fs, ref dataHash);
            using (var fs = new FileStream(pathPos, FileMode.Create, FileAccess.Write))
                GaussianSplatAssetBuilder.CreatePositionsData(inputSplats, m_FormatPos, fs, ref dataHash);
            using (var fs = new FileStream(pathOther, FileMode.Create, FileAccess.Write))
                GaussianSplatAssetBuilder.CreateOtherData(inputSplats, m_FormatScale, fs, ref dataHash, splatSHIndices);
            using (var fs = new FileStream(pathCol, FileMode.Create, FileAccess.Write))
                GaussianSplatAssetBuilder.CreateColorData(inputSplats, m_FormatColor, fs, ref dataHash, (tex, fmt) => EditorUtility.CompressTexture(tex, fmt, 100));
            using (var fs = new FileStream(pathSh, FileMode.Create, FileAccess.Write))
                GaussianSplatAssetBuilder.CreateSHData(inputSplats, m_FormatSH, fs, ref dataHash, clusteredSHs);
            asset.SetDataHash(dataHash);

            splatSHIndices.Dispose();
            clusteredSHs.Dispose();

            // files are created, import them so we can get to the imported objects, ugh
            EditorUtility.DisplayProgressBar(kProgressTitle, "Initial texture import", 0.85f);
            AssetDatabase.Refresh(ImportAssetOptions.ForceUncompressedImport);

            EditorUtility.DisplayProgressBar(kProgressTitle, "Setup data onto asset", 0.95f);
            asset.SetAssetFiles(
                useChunks ? AssetDatabase.LoadAssetAtPath<TextAsset>(pathChunk) : null,
                AssetDatabase.LoadAssetAtPath<TextAsset>(pathPos),
                AssetDatabase.LoadAssetAtPath<TextAsset>(pathOther),
                AssetDatabase.LoadAssetAtPath<TextAsset>(pathCol),
                AssetDatabase.LoadAssetAtPath<TextAsset>(pathSh),
                AssetDatabase.LoadAssetAtPath<TextAsset>(pathTemporal));

            var assetPath = $"{m_OutputFolder}/{baseName}.asset";
            var savedAsset = CreateOrReplaceAsset(asset, assetPath);

            EditorUtility.DisplayProgressBar(kProgressTitle, "Saving assets", 0.99f);
            AssetDatabase.SaveAssets();
            EditorUtility.ClearProgressBar();

            Selection.activeObject = savedAsset;
        }

        NativeArray<InputSplatData> LoadInputSplatFile(string filePath)
        {
            NativeArray<InputSplatData> data = default;
            if (!File.Exists(filePath))
            {
                m_ErrorMessage = $"Did not find {filePath} file";
                return data;
            }
            try
            {
                GaussianFileReader.ReadFile(filePath, out data, out m_TemporalKind, out m_FtgsInfo);
            }
            catch (Exception ex)
            {
                m_ErrorMessage = ex.Message;
            }
            return data;
        }

        [BurstCompile]
        static unsafe void GatherSHs(int splatCount, InputSplatData* splatData, float* shData)
        {
            for (int i = 0; i < splatCount; ++i)
            {
                UnsafeUtility.MemCpy(shData, ((float*)splatData) + 9, 15 * 3 * sizeof(float));
                splatData++;
                shData += 15 * 3;
            }
        }

        [BurstCompile]
        struct ConvertSHClustersJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> m_Input;
            public NativeArray<GaussianSplatAsset.SHTableItemFloat16> m_Output;
            public void Execute(int index)
            {
                var addr = index * 15;
                GaussianSplatAsset.SHTableItemFloat16 res;
                res.sh1 = new half3(m_Input[addr+0]);
                res.sh2 = new half3(m_Input[addr+1]);
                res.sh3 = new half3(m_Input[addr+2]);
                res.sh4 = new half3(m_Input[addr+3]);
                res.sh5 = new half3(m_Input[addr+4]);
                res.sh6 = new half3(m_Input[addr+5]);
                res.sh7 = new half3(m_Input[addr+6]);
                res.sh8 = new half3(m_Input[addr+7]);
                res.sh9 = new half3(m_Input[addr+8]);
                res.shA = new half3(m_Input[addr+9]);
                res.shB = new half3(m_Input[addr+10]);
                res.shC = new half3(m_Input[addr+11]);
                res.shD = new half3(m_Input[addr+12]);
                res.shE = new half3(m_Input[addr+13]);
                res.shF = new half3(m_Input[addr+14]);
                res.shPadding = default;
                m_Output[index] = res;
            }
        }
        static bool ClusterSHProgress(float val)
        {
            EditorUtility.DisplayProgressBar(kProgressTitle, $"Cluster SHs ({val:P0})", 0.2f + val * 0.5f);
            return true;
        }

        static unsafe void ClusterSHs(NativeArray<InputSplatData> splatData, GaussianSplatAsset.SHFormat format, out NativeArray<GaussianSplatAsset.SHTableItemFloat16> shs, out NativeArray<int> shIndices)
        {
            shs = default;
            shIndices = default;

            int shCount = GaussianSplatAsset.GetSHCount(format, splatData.Length);
            if (shCount >= splatData.Length) // no need to cluster, just use raw data
                return;

            const int kShDim = 15 * 3;
            const int kBatchSize = 2048;
            float passesOverData = format switch
            {
                GaussianSplatAsset.SHFormat.Cluster64k => 0.3f,
                GaussianSplatAsset.SHFormat.Cluster32k => 0.4f,
                GaussianSplatAsset.SHFormat.Cluster16k => 0.5f,
                GaussianSplatAsset.SHFormat.Cluster8k => 0.8f,
                GaussianSplatAsset.SHFormat.Cluster4k => 1.2f,
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
            };

            float t0 = Time.realtimeSinceStartup;
            NativeArray<float> shData = new(splatData.Length * kShDim, Allocator.Persistent);
            GatherSHs(splatData.Length, (InputSplatData*) splatData.GetUnsafeReadOnlyPtr(), (float*) shData.GetUnsafePtr());

            NativeArray<float> shMeans = new(shCount * kShDim, Allocator.Persistent);
            shIndices = new(splatData.Length, Allocator.Persistent);

            KMeansClustering.Calculate(kShDim, shData, kBatchSize, passesOverData, ClusterSHProgress, shMeans, shIndices);
            shData.Dispose();

            shs = new NativeArray<GaussianSplatAsset.SHTableItemFloat16>(shCount, Allocator.Persistent);

            ConvertSHClustersJob job = new ConvertSHClustersJob
            {
                m_Input = shMeans.Reinterpret<float3>(4),
                m_Output = shs
            };
            job.Schedule(shCount, 256).Complete();
            shMeans.Dispose();
            float t1 = Time.realtimeSinceStartup;
            Debug.Log($"GS: clustered {splatData.Length/1000000.0:F2}M SHs into {shCount/1024}K ({passesOverData:F1}pass/{kBatchSize}batch) in {t1-t0:F0}s");
        }

        static GaussianSplatAsset.CameraInfo[] LoadJsonCamerasFile(string curPath, bool doImport)
        {
            if (!doImport)
                return null;

            string camerasPath;
            while (true)
            {
                var dir = Path.GetDirectoryName(curPath);
                if (!Directory.Exists(dir))
                    return null;
                camerasPath = $"{dir}/{kCamerasJson}";
                if (File.Exists(camerasPath))
                    break;
                curPath = dir;
            }

            if (!File.Exists(camerasPath))
                return null;

            string json = File.ReadAllText(camerasPath);
            var jsonCameras = JSONParser.FromJson<List<JsonCamera>>(json);
            if (jsonCameras == null || jsonCameras.Count == 0)
                return null;

            var result = new GaussianSplatAsset.CameraInfo[jsonCameras.Count];
            for (var camIndex = 0; camIndex < jsonCameras.Count; camIndex++)
            {
                var jsonCam = jsonCameras[camIndex];
                var pos = new Vector3(jsonCam.position[0], jsonCam.position[1], jsonCam.position[2]);
                // the matrix is a "view matrix", not "camera matrix" lol
                var axisx = new Vector3(jsonCam.rotation[0][0], jsonCam.rotation[1][0], jsonCam.rotation[2][0]);
                var axisy = new Vector3(jsonCam.rotation[0][1], jsonCam.rotation[1][1], jsonCam.rotation[2][1]);
                var axisz = new Vector3(jsonCam.rotation[0][2], jsonCam.rotation[1][2], jsonCam.rotation[2][2]);

                axisy *= -1;
                axisz *= -1;

                var cam = new GaussianSplatAsset.CameraInfo
                {
                    pos = pos,
                    axisX = axisx,
                    axisY = axisy,
                    axisZ = axisz,
                    fov = 25 //@TODO
                };
                result[camIndex] = cam;
            }

            return result;
        }

        [Serializable]
        public class JsonCamera
        {
            public int id;
            public string img_name;
            public int width;
            public int height;
            public float[] position;
            public float[][] rotation;
            public float fx;
            public float fy;
        }

    }
}