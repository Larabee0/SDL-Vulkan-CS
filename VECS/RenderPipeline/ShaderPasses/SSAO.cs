using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vortice.Vulkan;

namespace VECS
{
    public class SSAO
    {
        public static readonly int SSAO_RT_PropertyId = "ssao_Source".GetShaderPropertyId();
        public static readonly int SSAO_Blur_RT_PropertyId = "ssao_blur_Source".GetShaderPropertyId();
        public static readonly int SSAO_Kernals_PropertyId = "ssaoKernels".GetShaderPropertyId();
        public static readonly int SSAO_Noise_PropertyId = "ssaoNoise".GetShaderPropertyId();

        private readonly IRenderer ActiveRenderer;

        private readonly ComputeVariant _computeSSAOGenerate;
        private readonly ComputeVariant _computeSSAOBlur;

        private readonly RenderTargetDefintion _ssaoRTDef = new("SSAO_RT", SSAO_RT_PropertyId, VkFormat.R8Unorm, -1,
                VkImageUsageFlags.Storage,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.ColorAttachmentOptimal,
                VkImageLayout.General,
                VkImageLayout.ShaderReadOnlyOptimal,
                new(0, 0, 0, 0));

        private RenderTarget _ssaoRT;


        private RenderTarget _ssaoBlurRt;
        public static bool SSAO_Enabled = true;

        private bool _SSAO_Cleared = false;

        public SSAO(IRenderer activeRenderer)
        {
            ActiveRenderer = activeRenderer;
            GenerateResources();

            GraphicsPipelineConfigInfo configInfo = GraphicsPipelineConfigInfo.DefaultPipelineConfigInfo([], []);
            configInfo.depthStencilInfo.depthTestEnable = false;
            configInfo.colourFormats = [VkFormat.R8Unorm];
            _computeSSAOGenerate = ComputePipeline.GetOrCreate("ssao_generate.comp").Default();
            _computeSSAOBlur = ComputePipeline.GetOrCreate("ssao_blur.comp").Default();
            _computeSSAOGenerate.PushConstantsHandler.SetPushConstantFloat("radius", 0, 0.5f);
            _computeSSAOGenerate.PushConstantsHandler.SetPushConstantFloat("bias", 0, 0.025f);
            _computeSSAOGenerate.PushConstantsHandler.SetPushConstantInt("kernelSize", 0, 64);
            _computeSSAOGenerate?.SetStorageBuffer(ShaderProperties.CameraDataId, EngineBuffers.TryGetBuffer(ShaderProperties.CameraDataId));
            _computeSSAOGenerate?.SetStorageBuffer(SSAO_Kernals_PropertyId, EngineBuffers.TryGetBuffer(SSAO_Kernals_PropertyId));

            RenderGraph.AddResource(new("SSAO_BLUR_RT", SSAO_Blur_RT_PropertyId, VkFormat.R8Unorm, 0,
                VkImageUsageFlags.Storage,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.ColorAttachmentOptimal,
                VkImageLayout.General,
                VkImageLayout.ShaderReadOnlyOptimal,
                new(1, 1, 1, 1)));

            RenderGraph.AddPass("SSAO_Generate", PassType.Compute, ["DeferredObjectsPass", "DeferredDepthOnlyPass", "SSAO_Clear"], ["G_PositionAttachment", "G_NormalAttachment", "MainDepthAttachment"], ["SSAO_RT"], GenerateSSAO);
            RenderGraph.AddPass("SSAO_Blur", PassType.Compute, ["SSAO_Generate"], ["SSAO_RT"], ["SSAO_BLUR_RT"], BlurSSAO);
            RenderGraph.AddPass("SSAO_Clear", PassType.Compute, ["DeferredObjectsPass", "DeferredDepthOnlyPass"], [""], ["SSAO_BLUR_RT"], ClearSSAO);
            RenderGraph.DisablePass("SSAO_Clear");
        }

        public void RecreateRenderTargets()
        {
            var windowExtents = Application.MainWindow.WindowExtent;
            _ssaoRT = RenderGraph.GetResource("SSAO_RT");
            _ssaoBlurRt = RenderGraph.GetResource("SSAO_BLUR_RT");
            bool noRenderGraphResource = _ssaoRT == null;
            _ssaoRT = IRenderer.CreateOrUpdateRT(_ssaoRT, _ssaoRTDef, new(windowExtents.width / 2, windowExtents.height / 2));
            if (noRenderGraphResource)
            {
                RenderGraph.AddResource("SSAO_RT", _ssaoRT);
            }

            _computeSSAOGenerate.PushConstantsHandler.SetPushConstantVector2("outputImageSize", 0, new(windowExtents.width / 2, windowExtents.height / 2));
            _computeSSAOBlur.PushConstantsHandler.SetPushConstantVector2("outputImageSize", 0, new(windowExtents.width, windowExtents.height));
            _SSAO_Cleared = false;
        }

        private static void GenerateResources()
        {
            GPUBuffer<Vector4> ssaoKernelBuffer = new(64, VkBufferUsageFlags.StorageBuffer, true, false, false);

            var ssaoKernels = ssaoKernelBuffer.HostBuffer;

            for (int i = 0; i < ssaoKernels.Length; i++)
            {
                Vector3 sample = new(Random.Shared.NextSingle()*2.0f-1.0f, Random.Shared.NextSingle() * 2.0f - 1.0f, Random.Shared.NextSingle());
                sample = Vector3.Normalize(sample);
                sample *= Random.Shared.NextSingle();
                
                float scale = (float)i / 64.0f;
                scale = NumericsExtensions.Lerp(0.1f, 1.0f, scale * scale);
                sample *= scale;
                ssaoKernels[i] = sample.AsVector4();
            }
            ssaoKernelBuffer.WriteFromHostBuffer();
            _= new GPUBufferAsset("SSAO_Kernels", ssaoKernelBuffer);
            SwapChainBufferAsset swb = new("SSAO_Kernal", SwapChainBuffer.AliasGPUBuffer(ssaoKernelBuffer));
            EngineBuffers.AddOrUpdateEngineBuffer(SSAO_Kernals_PropertyId, swb);

            Texture2D ssaoNoiseTex = new("SSAO_Noise", 8, 8, VkFormat.R32G32Sfloat, VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst, VkSamplerAddressMode.Repeat, false);
            GPUBuffer<Vector2> ssaoNoiseBuffer = new(64, VkBufferUsageFlags.TransferSrc, true, false, false);
            var ssaoNoise = ssaoNoiseBuffer.HostBuffer;

            for (int i = 0; i < ssaoNoise.Length; i++)
            {
                Vector2 noise = new(Random.Shared.NextSingle() * 2.0f - 1.0f, Random.Shared.NextSingle() * 2.0f - 1.0f);

                ssaoNoise[i] = noise;
            }
            ssaoNoiseBuffer.WriteFromHostBuffer();
            ssaoNoiseTex.CopyFromBuffer(ssaoNoiseBuffer,true);

            EngineTextures.AddOrUpdateTexture(SSAO_Noise_PropertyId, (SingleTexture)ssaoNoiseTex);
        }

        private void GenerateSSAO(RendererFrameInfo frameInfo)
        {
            _computeSSAOGenerate.PushConstantsHandler.SetPushConstantUInt("cameraIndex", 0, 0);
            _computeSSAOGenerate.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, (uint)_ssaoRT.Target.Width, (uint)_ssaoRT.Target.Height);
        }

        private void BlurSSAO(RendererFrameInfo frameInfo)
        {
            _computeSSAOBlur.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, (uint)_ssaoBlurRt.Target.Width, (uint)_ssaoBlurRt.Target.Height);

            _SSAO_Cleared = false;
        }

        private void ClearSSAO(RendererFrameInfo frameInfo)
        {
            if (!_SSAO_Cleared)
            {
                _SSAO_Cleared = true;
                _ssaoBlurRt.ClearAttachment(frameInfo.CommandBuffer);
                RenderGraph.DisablePass("SSAO_Clear");
            }
        }

        public static void SSAO_Toggle_Input()
        {
            if (InputManager.Instance.GetKeyUp(SDL3.SDL_Keycode.O))
            {
                SSAO_Enabled = !SSAO_Enabled;
                Console.WriteLine("SSAO Enabled: {0}", SSAO_Enabled);
                if (SSAO_Enabled)
                {
                    RenderGraph.EnablePasses(["SSAO_Blur", "SSAO_Generate"]);
                }
                else
                {
                    RenderGraph.EnablePass("SSAO_Clear");
                    RenderGraph.DisablePasses(["SSAO_Blur", "SSAO_Generate"]);
                }
            }
        }

    }
}
