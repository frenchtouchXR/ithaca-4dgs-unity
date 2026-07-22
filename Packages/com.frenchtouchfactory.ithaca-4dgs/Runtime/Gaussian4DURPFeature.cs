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

            class PassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle SourceTexture;
                internal TextureHandle SourceDepth;
                internal TextureHandle GaussianSplatRT;
                internal TextureHandle GaussianSplatDepthRT;
            }
            class DepthPassData
            {
                internal TextureHandle SplatDepthRT;
            }
            static Material s_depthWriteMat;

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                var textureHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, Gaussian4DSplatRTName, true);

                RenderTextureDescriptor depthRtDesc = cameraData.cameraTargetDescriptor;
                depthRtDesc.depthBufferBits = 0;
                depthRtDesc.msaaSamples = 1;
                depthRtDesc.graphicsFormat = GraphicsFormat.R16_SFloat;
                var depthRtHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthRtDesc, Gaussian4DSplatDepthRTName, true);

                var activeColor = resourceData.activeColorTexture;
                var activeDepth = resourceData.activeDepthTexture;

                using (var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData))
                {
                    passData.CameraData = cameraData;
                    passData.SourceTexture = activeColor;
                    passData.SourceDepth = activeDepth;
                    passData.GaussianSplatRT = textureHandle;
                    passData.GaussianSplatDepthRT = depthRtHandle;

                    builder.UseTexture(activeColor, AccessFlags.ReadWrite);
                    builder.UseTexture(activeDepth);
                    builder.UseTexture(textureHandle, AccessFlags.Write);
                    builder.UseTexture(depthRtHandle, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                    {
                        var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        using var _ = new ProfilingScope(commandBuffer, s_profilingSampler);
                        commandBuffer.SetGlobalTexture(s_gaussian4DSplatRT, data.GaussianSplatRT);
                        commandBuffer.SetGlobalTexture(s_gaussian4DSplatDepthRT, data.GaussianSplatDepthRT);
                        var mrt = new RenderTargetIdentifier[] { data.GaussianSplatRT, data.GaussianSplatDepthRT };
                        CoreUtils.SetRenderTarget(commandBuffer, mrt, data.SourceDepth, ClearFlag.Color, Color.clear);
                        Material matComposite = Gaussian4DSplatRenderSystem.instance.SortAndRenderSplats(data.CameraData.camera, commandBuffer);
                        commandBuffer.BeginSample(Gaussian4DSplatRenderSystem.s_ProfCompose);
                        Blitter.BlitCameraTexture(commandBuffer, data.GaussianSplatRT, data.SourceTexture, matComposite, 0);
                        commandBuffer.EndSample(Gaussian4DSplatRenderSystem.s_ProfCompose);
                    });
                }

                if (s_depthWriteMat == null)
                {
                    var sh = Shader.Find("Hidden/Gaussian Splatting/DepthWrite");
                    if (sh != null) s_depthWriteMat = CoreUtils.CreateEngineMaterial(sh);
                }
                if (s_depthWriteMat != null)
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
        }

        GSRenderPass m_Pass;
        bool m_HasCamera;

        public override void Create()
        {
            m_Pass = new GSRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
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
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass = null;
        }
    }
}

#endif // #if GS_ENABLE_URP
