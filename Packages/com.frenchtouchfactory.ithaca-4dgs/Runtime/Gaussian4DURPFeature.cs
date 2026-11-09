// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

#if !UNITY_6000_0_OR_NEWER
#error Unity Gaussian Splatting URP support only works in Unity 6 or later
#endif

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace GaussianSplatting4D.Runtime
{
    // Note: I have no idea what is the purpose of ScriptableRendererFeature vs ScriptableRenderPass, which one of those
    // is supposed to do resource management vs logic, etc. etc. Code below "seems to work" but I'm just fumbling along,
    // without understanding any of it.
    //
    // ReSharper disable once InconsistentNaming
    class Gaussian4DURPFeature : ScriptableRendererFeature
    {
        class GSRenderPass : ScriptableRenderPass
        {
            const string Gaussian4DSplatRTName = "_Gaussian4DSplatRT";
            const string Gaussian4DSplatDepthRTName = "_Gaussian4DSplatDepthRT";

            const string ProfilerTag = "GaussianSplatRenderGraph";
            static readonly ProfilingSampler s_profilingSampler = new(ProfilerTag);
            static readonly int s_gaussian4DSplatRT = Shader.PropertyToID(Gaussian4DSplatRTName);
            static readonly int s_gaussian4DSplatDepthRT = Shader.PropertyToID(Gaussian4DSplatDepthRTName);
            static readonly int s_CompositeFlipY = Shader.PropertyToID("_Gaussian4DCompositeFlipY");
            static readonly int s_TargetSize = Shader.PropertyToID("_Gaussian4DTargetSize");
            static readonly int s_EyeIndex = Shader.PropertyToID("_Gaussian4DEyeIndex");

            class PassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle SourceTexture;
                internal TextureHandle SourceDepth;
                internal TextureHandle GaussianSplatRT;
                internal TextureHandle GaussianSplatDepthRT;
                internal bool Stereo;           // casque, deux yeux en une passe (cibles en tableau de 2 tranches)
                internal readonly EyeView[] Eyes = new EyeView[2];
                internal bool CompositeFlipY;   // image finale de la camera (orientation d'ecran) : lignes retournees
                // dans ce cas, la profondeur de la scene (orientation d'ecran) ne peut pas servir au test des gaussiennes
                // (dessinees en orientation de texture) : profondeur propre aux gaussiennes, videe a chaque vue
                internal TextureHandle OwnDepth;
                internal Vector4 TargetSize;
                internal bool UseOwnDepth;
                internal float SplatScale;
                internal Vector2 EyeSize;
            }
            static readonly int s_SplatScale = Shader.PropertyToID("_Gaussian4DSplatScale");
            static readonly int s_CompositeSize = Shader.PropertyToID("_Gaussian4DCompositeSize");
            static readonly System.Collections.Generic.List<Material> s_GroupComposites = new();
            class DepthPassData
            {
                internal TextureHandle SplatDepthRT;
            }
            static Material s_depthWriteMat;

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                // Casque en Single Pass Instanced / multiview : la camera est un tableau de 2 tranches (une par oeil).
                // Les splats sont alors tries une fois puis dessines oeil par oeil dans des cibles 2D, composes dans la
                // tranche de l'oeil : le shader de splats reste mono-vue.
                var xr = cameraData.xr;
                bool stereo = xr.enabled && xr.singlePassEnabled;

                RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                if (stereo) { rtDesc.dimension = TextureDimension.Tex2D; rtDesc.volumeDepth = 1; rtDesc.vrUsage = VRTextureUsage.None; }
                // casque : image des gaussiennes reduite (Gaussian4DSplatRenderer.stereoResolutionScale)
                var tailleOeil = new Vector2(rtDesc.width, rtDesc.height);
                float echelle = stereo ? Mathf.Clamp(Gaussian4DSplatRenderer.stereoResolutionScale, 0.1f, 1f) : 1f;
                if (echelle < 0.999f) { rtDesc.width = Mathf.Max(1, Mathf.RoundToInt(rtDesc.width * echelle)); rtDesc.height = Mathf.Max(1, Mathf.RoundToInt(rtDesc.height * echelle)); }
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                var textureHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, Gaussian4DSplatRTName, true);

                RenderTextureDescriptor depthRtDesc = cameraData.cameraTargetDescriptor;
                depthRtDesc.depthBufferBits = 0;
                depthRtDesc.msaaSamples = 1;
                depthRtDesc.graphicsFormat = GraphicsFormat.R16_SFloat;
                if (stereo) { depthRtDesc.dimension = TextureDimension.Tex2D; depthRtDesc.volumeDepth = 1; depthRtDesc.vrUsage = VRTextureUsage.None; depthRtDesc.width = rtDesc.width; depthRtDesc.height = rtDesc.height; }
                var depthRtHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthRtDesc, Gaussian4DSplatDepthRTName, true);

                var activeColor = resourceData.activeColorTexture;
                var activeDepth = resourceData.activeDepthTexture;
                bool compositeFlipY = SystemInfo.graphicsUVStartsAtTop && resourceData.isActiveTargetBackBuffer;
                TextureHandle ownDepth = TextureHandle.nullHandle;
                // casque (deux yeux en une passe) : la profondeur de la scene d'un oeil n'est pas fiable comme attache de
                // rendu (tranche ignoree, l'oeil droit testait contre celle du gauche : personnage rogne dans un oeil) ;
                // les gaussiennes ont alors leur propre profondeur, videe pour chaque oeil
                bool ownDepthNeeded = compositeFlipY || stereo;
                if (ownDepthNeeded)
                {
                    RenderTextureDescriptor d = rtDesc;
                    d.graphicsFormat = GraphicsFormat.None;
                    d.depthStencilFormat = GraphicsFormat.D16_UNorm;
                    d.depthBufferBits = 16;
                    ownDepth = UniversalRenderer.CreateRenderGraphTexture(renderGraph, d, "_Gaussian4DSplatOwnDepth", true);
                }

                using (var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData))
                {
                    passData.CameraData = cameraData;
                    passData.SourceTexture = activeColor;
                    passData.SourceDepth = activeDepth;
                    passData.GaussianSplatRT = textureHandle;
                    passData.GaussianSplatDepthRT = depthRtHandle;
                    passData.Stereo = stereo;
                    // matrices de chaque vue : les gaussiennes sont toujours dessinees dans leur image intermediaire avec
                    // l'orientation d'une texture (la forme 2D des gaussiennes la suppose) ; si la camera dessine directement
                    // dans l'image finale (texture intermediaire « Auto », casque), la composition retourne les lignes.
                    // Les matrices globales d'URP s'y trompaient dans ce cas (modele tete en bas, ou gaussiennes etirees).
                    for (int eye = 0; eye < (stereo ? 2 : 1); ++eye)
                    {
                        var view = xr.enabled ? xr.GetViewMatrix(eye) : cameraData.camera.worldToCameraMatrix;
                        var proj = xr.enabled ? xr.GetProjMatrix(eye) : cameraData.camera.projectionMatrix;
                        passData.Eyes[eye] = new EyeView { view = view, gpuProjection = GL.GetGPUProjectionMatrix(proj, true), position = view.inverse.GetColumn(3),
                                                           screenSize = new Vector2(rtDesc.width, rtDesc.height) };
                    }
                    passData.CompositeFlipY = compositeFlipY;
                    // _ScreenParams dans le shader, comme avant ; image reduite : sa taille
                    passData.TargetSize = echelle < 0.999f ? new Vector4(rtDesc.width, rtDesc.height, 0, 0) : Vector4.zero;
                    passData.SplatScale = echelle;
                    passData.EyeSize = tailleOeil;
                    Gaussian4DSplatRenderer.lastPassInfo = $"{(stereo ? "deux yeux" : "une vue")} · camera {cameraData.cameraTargetDescriptor.width}x{cameraData.cameraTargetDescriptor.height} · " +
                        $"cible {(resourceData.isActiveTargetBackBuffer ? "finale" : "intermediaire")} · ecran {cameraData.camera.pixelWidth}x{cameraData.camera.pixelHeight} · " +
                        $"oeil {UnityEngine.XR.XRSettings.eyeTextureWidth}x{UnityEngine.XR.XRSettings.eyeTextureHeight} · retournement {compositeFlipY}";
                    passData.OwnDepth = ownDepth;
                    passData.UseOwnDepth = ownDepthNeeded;
                    if (ownDepthNeeded) builder.UseTexture(ownDepth, AccessFlags.Write);

                    builder.UseTexture(activeColor, AccessFlags.ReadWrite);
                    builder.UseTexture(activeDepth, stereo ? AccessFlags.ReadWrite : AccessFlags.Read);
                    builder.UseTexture(textureHandle, AccessFlags.Write);
                    builder.UseTexture(depthRtHandle, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                    {
                        var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        using var _ = new ProfilingScope(commandBuffer, s_profilingSampler);
                        commandBuffer.SetGlobalTexture(s_gaussian4DSplatRT, data.GaussianSplatRT);
                        commandBuffer.SetGlobalTexture(s_gaussian4DSplatDepthRT, data.GaussianSplatDepthRT);
                        commandBuffer.SetGlobalFloat(s_CompositeFlipY, data.CompositeFlipY ? 1f : 0f);
                        commandBuffer.SetGlobalVector(s_TargetSize, data.TargetSize);
                        commandBuffer.SetGlobalFloat(s_EyeIndex, -1);
                        commandBuffer.SetGlobalFloat(s_SplatScale, data.SplatScale);
                        commandBuffer.SetGlobalVector(s_CompositeSize, new Vector4(data.EyeSize.x, data.EyeSize.y, 0, 0));
                        if (data.Stereo)
                        {
                            RenderStereo(data, commandBuffer);
                            return;
                        }
                        var mrt = new RenderTargetIdentifier[] { data.GaussianSplatRT, data.GaussianSplatDepthRT };
                        var depthTarget = data.CompositeFlipY ? (RenderTargetIdentifier)data.OwnDepth : (RenderTargetIdentifier)data.SourceDepth;
                        CoreUtils.SetRenderTarget(commandBuffer, mrt, depthTarget, data.CompositeFlipY ? ClearFlag.All : ClearFlag.Color, Color.clear);
                        // Une composition par groupe d'objets de meme mode de melange (m_SRGBBlending), du plus lointain
                        // au plus proche. Un seul groupe dans le cas habituel : rendu identique a l'unique composition d'avant.
                        var system = Gaussian4DSplatRenderSystem.instance;
                        var groups = system.CompositeGroups();
                        for (int g = groups.Count - 1; g >= 0; --g)
                        {
                            if (g != groups.Count - 1)
                            {
                                // texture intermediaire videe pour le groupe suivant ; la profondeur des splats s'accumule
                                CoreUtils.SetRenderTarget(commandBuffer, (RenderTargetIdentifier)data.GaussianSplatRT, ClearFlag.Color, Color.clear);
                                CoreUtils.SetRenderTarget(commandBuffer, mrt, depthTarget);
                            }
                            Material matComposite = system.SortAndRenderSplats(data.CameraData.camera, commandBuffer, groups[g].start, groups[g].end, data.Eyes[0]);
                            commandBuffer.BeginSample(Gaussian4DSplatRenderSystem.s_ProfCompose);
                            Blitter.BlitCameraTexture(commandBuffer, data.GaussianSplatRT, data.SourceTexture, matComposite, 0);
                            commandBuffer.EndSample(Gaussian4DSplatRenderSystem.s_ProfCompose);
                        }
                    });
                }

                if (s_depthWriteMat == null)
                {
                    var sh = Shader.Find("Hidden/Gaussian Splatting/DepthWrite");
                    if (sh != null) s_depthWriteMat = CoreUtils.CreateEngineMaterial(sh);
                }
                if (s_depthWriteMat != null && !stereo && !compositeFlipY)   // stereo : ecrit oeil par oeil dans RenderStereo
                {
                    using (var depthBuilder = renderGraph.AddRasterRenderPass<DepthPassData>("Gaussian4DSplatDepthWrite", out DepthPassData dpd))
                    {
                        dpd.SplatDepthRT = depthRtHandle;
                        depthBuilder.UseTexture(depthRtHandle, AccessFlags.Read);
                        depthBuilder.SetRenderAttachmentDepth(activeDepth, AccessFlags.Write);
                        depthBuilder.AllowPassCulling(false);
                        depthBuilder.SetRenderFunc(static (DepthPassData data, RasterGraphContext ctx) =>
                        {
                            s_depthWriteMat.SetTexture(s_gaussian4DSplatDepthRT, data.SplatDepthRT);
                            ctx.cmd.DrawProcedural(Matrix4x4.identity, s_depthWriteMat, 0, MeshTopology.Triangles, 3, 1);
                        });
                    }
                }
            }

            // Deux yeux en une passe : tri unique depuis la camera, puis pour chaque oeil calcul de vue avec ses
            // matrices, dessin dans les cibles 2D, composition et ecriture de profondeur dans la tranche de l'oeil.
            static void RenderStereo(PassData data, CommandBuffer cmd)
            {
                var xr = data.CameraData.xr;
                var cam = data.CameraData.camera;
                xr.StopSinglePass(cmd);   // dessins et compositions mono-vue, tranche par tranche

                var system = Gaussian4DSplatRenderSystem.instance;
                var groups = system.CompositeGroups();
                s_GroupComposites.Clear();
                for (int g = 0; g < groups.Count; ++g)
                    s_GroupComposites.Add(system.SortSplats(cam, cmd, groups[g].start, groups[g].end));

                var splat = (RenderTargetIdentifier)data.GaussianSplatRT;
                // profondeur des gaussiennes (2e cible) : relue seulement pour l'ecrire dans celle de la scene, ce qui
                // n'est pas fait avec la profondeur propre ; sans elle, chaque gaussienne n'ecrit plus qu'une image
                // (memoire de la carte graphique : le Quest ralentit avec la place du modele a l'ecran)
                var mrt = data.UseOwnDepth ? new RenderTargetIdentifier[] { splat } : new RenderTargetIdentifier[] { splat, data.GaussianSplatDepthRT };
                for (int eye = 0; eye < 2; ++eye)
                {
                    var ev = data.Eyes[eye];
                    var dstColor = new RenderTargetIdentifier((RenderTargetIdentifier)data.SourceTexture, 0, CubemapFace.Unknown, eye);
                    var dstDepth = new RenderTargetIdentifier((RenderTargetIdentifier)data.SourceDepth, 0, CubemapFace.Unknown, eye);
                    var testDepth = data.UseOwnDepth ? (RenderTargetIdentifier)data.OwnDepth : dstDepth;

                    cmd.SetRenderTarget(mrt, testDepth);
                    cmd.ClearRenderTarget(data.UseOwnDepth, true, Color.clear);
                    for (int g = groups.Count - 1; g >= 0; --g)
                    {
                        if (g != groups.Count - 1)
                        {
                            cmd.SetRenderTarget(splat);
                            cmd.ClearRenderTarget(false, true, Color.clear);
                            cmd.SetRenderTarget(mrt, testDepth);
                        }
                        system.DrawSplatsForEye(cam, cmd, groups[g].start, groups[g].end, ev);
                        cmd.BeginSample(Gaussian4DSplatRenderSystem.s_ProfCompose);
                        cmd.SetGlobalFloat(s_EyeIndex, eye);       // diagnostic : teinte par oeil (_Gaussian4DEyeDebug)
                        cmd.SetRenderTarget(dstColor);
                        if (s_GroupComposites[g] != null)
                            cmd.DrawProcedural(Matrix4x4.identity, s_GroupComposites[g], 0, MeshTopology.Triangles, 3, 1);
                        cmd.EndSample(Gaussian4DSplatRenderSystem.s_ProfCompose);
                    }
                    if (s_depthWriteMat != null && !data.UseOwnDepth)
                    {
                        s_depthWriteMat.SetTexture(s_gaussian4DSplatDepthRT, data.GaussianSplatDepthRT);
                        cmd.SetRenderTarget(dstColor, dstDepth);
                        cmd.DrawProcedural(Matrix4x4.identity, s_depthWriteMat, 0, MeshTopology.Triangles, 3, 1);
                    }
                }
                xr.StartSinglePass(cmd);
            }
        }

        // vues hors ecran (Gaussian4DSplatRenderer.offscreenViews) : une fois par image, avant les opaques de la camera
        class OffscreenPass : ScriptableRenderPass
        {
            class Data { internal UniversalCameraData CameraData; internal bool Stereo; }
            static readonly int s_TargetSize = Shader.PropertyToID("_Gaussian4DTargetSize");
            static readonly int s_AuxDepth = Shader.PropertyToID("_Gaussian4DOffscreenDepth");
            internal static int s_LastFrame = -1;

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                using var builder = renderGraph.AddUnsafePass("Gaussian4DOffscreenViews", out Data data);
                data.CameraData = cameraData;
                data.Stereo = cameraData.xr.enabled && cameraData.xr.singlePassEnabled;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (Data data, UnsafeGraphContext context) =>
                {
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    if (data.Stereo) data.CameraData.xr.StopSinglePass(cmd);
                    var system = Gaussian4DSplatRenderSystem.instance;
                    int n = system.ActiveSplatCount;
                    foreach (var v in Gaussian4DSplatRenderer.offscreenViews)
                    {
                        if (!v.enabled || v.target == null) continue;
                        int w = v.target.width, h = v.target.height;
                        cmd.GetTemporaryRT(s_AuxDepth, w, h, 0, FilterMode.Point, GraphicsFormat.R16_SFloat);
                        var ev = new EyeView { view = v.view, gpuProjection = GL.GetGPUProjectionMatrix(v.projection, true),
                                               position = v.view.inverse.GetColumn(3), screenSize = new Vector2(w, h) };
                        cmd.SetGlobalVector(s_TargetSize, new Vector4(w, h, 0, 0));
                        cmd.SetRenderTarget(new RenderTargetIdentifier[] { v.target, s_AuxDepth }, v.target.depthBuffer);
                        cmd.ClearRenderTarget(true, true, Color.clear);
                        system.DrawSplatsForEye(data.CameraData.camera, cmd, 0, n, ev);
                        cmd.ReleaseTemporaryRT(s_AuxDepth);
                        v.after?.Invoke(cmd);
                    }
                    cmd.SetGlobalVector(s_TargetSize, Vector4.zero);
                    if (data.Stereo) data.CameraData.xr.StartSinglePass(cmd);
                });
            }
        }

        GSRenderPass m_Pass;
        OffscreenPass m_Offscreen;
        bool m_HasCamera;

        public override void Create()
        {
            m_Pass = new GSRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
            m_Offscreen = new OffscreenPass { renderPassEvent = RenderPassEvent.BeforeRenderingOpaques };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = false;
            var system = Gaussian4DSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cameraData.camera))
                return;

            m_HasCamera = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!m_HasCamera)
                return;
            // vues hors ecran : une fois par image, avec la premiere camera de jeu qui voit des gaussiennes
            var cam = renderingData.cameraData.camera;
            if (Gaussian4DSplatRenderer.offscreenViews.Count > 0 && cam.cameraType == CameraType.Game &&
                !Gaussian4DSplatRenderer.coverageOnlyCameras.Contains(cam) && OffscreenPass.s_LastFrame != Time.frameCount)
            {
                OffscreenPass.s_LastFrame = Time.frameCount;
                renderer.EnqueuePass(m_Offscreen);
            }
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass = null;
        }
    }
}

#endif // #if GS_ENABLE_URP
