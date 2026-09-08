using BCnEncoder.Shared.ImageFiles;
using System;
using System.IO;
using System.Numerics;
using VECS.LowLevel;
using Vortice.Vulkan;

namespace VECS
{
    public class SMAA
    {
        private readonly Texture2D AreaTexture;
        private readonly Texture2D SearchTexture;

        private readonly Material EdgeDetection;
        private readonly Material BlendWeightCalc;
        private readonly Material NeighbourhoodBlending;
#if DEBUG
        private readonly Material BlitEdgeTarget;
        private readonly Material BlitBlendTarget;
#endif
        private readonly IRenderer ActiveRenderer;

        private RenderTarget EdgeTarget;
        private RenderTarget BlendTarget;
        private RenderTarget PostProcessingAttachment;

        private bool _smaaEnabled = true;

        private static Texture2D DirectKTXLoad(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var fileStream = File.OpenRead(filePath);
            var ktxFile = KtxFile.Load(fileStream);
            fileStream.Close();
            
            var tex = new Texture2D(Path.GetFileNameWithoutExtension(filePath), (int)ktxFile.header.PixelWidth, (int)ktxFile.header.PixelHeight, ktxFile.header.GlInternalFormat.GetVkFormat(), VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled, false);

            tex.CopyFromArray(ktxFile.MipMaps[0].Faces[0].Data);

            return tex;
        }

        private static Texture2D DirectLoad(string filePath, VkFormat format)
        {
            return TextureLoader.Load2D(filePath, format);
        }

        public SMAA(IRenderer activeRenderer)
        {
            ActiveRenderer = activeRenderer;

            //SearchTexture = DirectLoad(Path.Combine(TextureLoader.DefaultTexturePath, "SearchTex.tga"), VkFormat.R8Unorm);
            //AreaTexture = DirectLoad(Path.Combine(TextureLoader.DefaultTexturePath, "AreaTex.tga"), VkFormat.R8G8B8A8Unorm);
            SearchTexture = DirectKTXLoad(Path.Combine(TextureLoader.DefaultTexturePath, "SearchTex.ktx"));
            AreaTexture = DirectKTXLoad(Path.Combine(TextureLoader.DefaultTexturePath, "AreaTex.ktx"));

            SearchTexture.SetImageLayout(VkImageLayout.ShaderReadOnlyOptimal, VkPipelineStageFlags2.Transfer, VkPipelineStageFlags2.FragmentShader);
            AreaTexture.SetImageLayout(VkImageLayout.ShaderReadOnlyOptimal, VkPipelineStageFlags2.Transfer, VkPipelineStageFlags2.FragmentShader);

            var pipelineConfig = GraphicsPipelineConfigInfo.DefaultPipelineConfigInfo([], []);

            pipelineConfig.rasterizationInfo.cullMode = VkCullModeFlags.None;
            pipelineConfig.rasterizationInfo.frontFace = VkFrontFace.Clockwise;
            pipelineConfig.colourFormats = [VkFormat.B10G11R11UfloatPack32];
            NeighbourhoodBlending = GraphicsPipeline.VertexFragmentPipeline("SMAA_Blending", "smaa_neighbourhood_blending.vert", "smaa_neighbourhood_blending.frag", pipelineConfig).Default();
            //NeighbourhoodBlending = GraphicsPipeline.VertexFragmentPipeline("SMAA_Blending", "smaa_neighbor.hlsl.vert", "smaa_neighbor.hlsl.frag", pipelineConfig).Default();

            pipelineConfig.colourFormats = [VkFormat.R8G8B8A8Unorm];
            pipelineConfig.depthStencilInfo.depthTestEnable = false;

            EdgeDetection = GraphicsPipeline.VertexFragmentPipeline("SMAA_Edge", "smaa_edge_detection.vert", "smaa_edge_detection.frag", pipelineConfig).Default();
            //EdgeDetection = GraphicsPipeline.VertexFragmentPipeline("SMAA_Edge", "smaa_edge.hlsl.vert", "smaa_edge.hlsl.frag", pipelineConfig).Default();
            
            BlendWeightCalc = GraphicsPipeline.VertexFragmentPipeline("SMAA_BlendWeight", "smaa_blending_weight.vert", "smaa_blending_weight.frag", pipelineConfig).Default();
            //BlendWeightCalc = GraphicsPipeline.VertexFragmentPipeline("SMAA_BlendWeight", "smaa_blend.hlsl.vert", "smaa_blend.hlsl.frag", pipelineConfig).Default();

            BlendWeightCalc.SetTexture("uAreaTexture".GetShaderPropertyId(), AreaTexture);
            BlendWeightCalc.SetTexture("uSearchTexture".GetShaderPropertyId(), SearchTexture);


#if DEBUG
            var alphaBlending = GraphicsPipelineConfigInfo.DefaultPipelineConfigInfo([], []);
            alphaBlending.colourFormats = [VkFormat.R32G32B32A32Sfloat];
            alphaBlending.depthStencilInfo.depthTestEnable = false;

            GraphicsPipeline smaaBlit = GraphicsPipeline.VertexFragmentPipeline("SMAA_Blitter", "fullscreen.vert", "blit.frag", alphaBlending);
            BlitEdgeTarget = smaaBlit.Default();
            BlitBlendTarget = smaaBlit.Create("SMAA_BlitBlendTarget");
#endif

            RenderGraph.AddResource(new("SMAA_Edge_Attachment",
                VkFormat.R8G8B8A8Unorm, 0,
                VkImageUsageFlags.None,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.ColorAttachmentOptimal,
                VkImageLayout.General,
                VkImageLayout.General,
                new(0, 0, 0, 0)));

            RenderGraph.AddResource(new("SMAA_Blend_Attachment",
                VkFormat.R8G8B8A8Unorm, 0,
                VkImageUsageFlags.None,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.ColorAttachmentOptimal,
                VkImageLayout.General,
                VkImageLayout.General,
                new(0, 0, 0, 0)));

            

            RenderGraph.AddPass("SMAA_Edge_Detection", PassType.Render, PassCategory.AntiAliasing, ["ForwardPass", "DeferredCompositePass", "TransaprentComposite"], ["MainColourAttachment"], ["SMAA_Edge_Attachment"], EdgeDetectionPass);
            RenderGraph.AddPass("SMAA_Blend_Weight", PassType.Render, PassCategory.AntiAliasing, ["SMAA_Edge_Detection"], ["SMAA_Edge_Attachment"], ["SMAA_Blend_Attachment"], BlendWeightCalculation);
            RenderGraph.AddPass("SMAA_Output", PassType.Render, PassCategory.AntiAliasing, ["SMAA_Blend_Weight"], ["SMAA_Blend_Attachment"], ["PostProcessingColourAttachment"], OutputBlending);

            VkSamplerCreateInfo samplerCreateInfo = new()
            {
                magFilter = VkFilter.Nearest,
                minFilter = VkFilter.Nearest,
                mipmapMode = VkSamplerMipmapMode.Nearest,
                addressModeU = VkSamplerAddressMode.ClampToEdge,
                addressModeV = VkSamplerAddressMode.ClampToEdge,
                addressModeW = VkSamplerAddressMode.ClampToEdge,
                mipLodBias = 0,
                anisotropyEnable = false,
                maxAnisotropy = 1.0f,
                compareEnable = false,
                compareOp = VkCompareOp.Never,
                minLod = 0,
                maxLod = float.MaxValue,
                borderColor = VkBorderColor.FloatTransparentBlack,
                unnormalizedCoordinates = false

            };
            EdgeDetection.SetSampler("PointSampler".GetShaderPropertyId(), TextureExtensions.GetOrCreateSample(samplerCreateInfo));
            EdgeDetection.SetSampler("uSampler".GetShaderPropertyId(), TextureExtensions.GetOrCreateSample(samplerCreateInfo));
            BlendWeightCalc.SetSampler("PointSampler".GetShaderPropertyId(), TextureExtensions.GetOrCreateSample(samplerCreateInfo));
            samplerCreateInfo = new()
            {
                magFilter = VkFilter.Linear,
                minFilter = VkFilter.Linear,
                mipmapMode = VkSamplerMipmapMode.Nearest,
                addressModeU = VkSamplerAddressMode.ClampToEdge,
                addressModeV = VkSamplerAddressMode.ClampToEdge,
                addressModeW = VkSamplerAddressMode.ClampToEdge,
                mipLodBias = 0,
                anisotropyEnable = false,
                maxAnisotropy = 1.0f,
                compareEnable = false,
                compareOp = VkCompareOp.Never,
                minLod = 0,
                maxLod = float.MaxValue,
                borderColor = VkBorderColor.FloatTransparentBlack,
                unnormalizedCoordinates = false

            };
            EdgeDetection.SetSampler("LinearSampler".GetShaderPropertyId(), TextureExtensions.GetOrCreateSample(samplerCreateInfo));
            //BlendWeightCalc.SetSampler("uSampler".GetShaderPropertyId(), TextureExtensions.GetOrCreateSample(samplerCreateInfo));
            BlendWeightCalc.SetSampler("LinearSampler".GetShaderPropertyId(), TextureExtensions.GetOrCreateSample(samplerCreateInfo));
            NeighbourhoodBlending.SetSampler("LinearSampler".GetShaderPropertyId(), TextureExtensions.GetOrCreateSample(samplerCreateInfo));
            BlendWeightCalc.SetSampler("uSampler".GetShaderPropertyId(), TextureExtensions.GetOrCreateSample(samplerCreateInfo));
            NeighbourhoodBlending.SetSampler("uSampler".GetShaderPropertyId(), TextureExtensions.GetOrCreateSample(samplerCreateInfo));
        }

        public void RecreateRenderTargets()
        {
            var windowExtents = Application.MainWindow.WindowExtent;

            EdgeTarget = RenderGraph.GetResource("SMAA_Edge_Attachment");
            BlendTarget = RenderGraph.GetResource("SMAA_Blend_Attachment");
            PostProcessingAttachment = RenderGraph.GetResource("PostProcessingColourAttachment");

            var texelSize = new Vector4(1.0f / windowExtents.width, 1.0f / windowExtents.height, windowExtents.width, windowExtents.height);

            EdgeDetection.PushConstants.SetPushConstantVector4("texelSize", 0, texelSize);
            EdgeDetection.SetVector4("texelSize.value".GetShaderPropertyId(), texelSize);
            EdgeDetection.SetTexture("uColourTexture".GetShaderPropertyId(), EngineTextures.TryGetTexture(ShaderProperties.MainColourAttachmentId));

            BlendWeightCalc.PushConstants.SetPushConstantVector4("texelSize", 0, texelSize);
            BlendWeightCalc.SetVector4("texelSize.value".GetShaderPropertyId(), texelSize);
            BlendWeightCalc.SetTexture("uColourTexture".GetShaderPropertyId(), EdgeTarget.Target);
            BlendWeightCalc.SetTexture("uEdgeTexture".GetShaderPropertyId(), EdgeTarget.Target);

            NeighbourhoodBlending.PushConstants.SetPushConstantVector4("texelSize", 0, texelSize);
            NeighbourhoodBlending.SetVector4("texelSize.value".GetShaderPropertyId(), texelSize);
            NeighbourhoodBlending.SetTexture("uBlendTexture".GetShaderPropertyId(), BlendTarget.Target);
            NeighbourhoodBlending.SetTexture("uColourTexture".GetShaderPropertyId(), EngineTextures.TryGetTexture(ShaderProperties.MainColourAttachmentId));
#if DEBUG
            BlitEdgeTarget.SetTexture("inputTexture".GetShaderPropertyId(), EdgeTarget.Target);
            BlitBlendTarget.SetTexture("inputTexture".GetShaderPropertyId(), BlendTarget.Target);
#endif
        }

        public void ApplyAA(RendererFrameInfo frameInfo)
        {
            _smaaEnabled = InputManager.Instance.GetKeyUp(SDL3.SDL_Keycode.F8) ? !_smaaEnabled : _smaaEnabled;

            if (!_smaaEnabled) return;

            var mainTarget = EngineTextures.TryGetTexture(ShaderProperties.MainColourAttachmentId);

            mainTarget.First.SetImageLayoutAuto(frameInfo.CommandBuffer, VkImageLayout.ShaderReadOnlyOptimal);

            EdgeDetectionPass(frameInfo);

            BlendWeightCalculation(frameInfo);

            mainTarget.First.SetImageLayoutAuto(frameInfo.CommandBuffer, VkImageLayout.ColorAttachmentOptimal);

            OutputBlending(frameInfo);

            // OutputEdgeDetection(frameInfo);

            // OutputBlendWeights(frameInfo);
        }

#if DEBUG
        private unsafe void OutputBlendWeights( RendererFrameInfo frameInfo)
        {
            var deferred = (DeferredRenderer)ActiveRenderer;
            deferred.StartForwardRendering(frameInfo.CommandBuffer, VkAttachmentLoadOp.Clear,true,true);

            BlitBlendTarget.Bind(frameInfo);
            GraphicsDevice.DeviceAPI.vkCmdDraw(frameInfo.CommandBuffer, 3, 1, 0, 0);

            deferred.EndForwardRendering(frameInfo);
        }

        private unsafe void OutputEdgeDetection(in RendererFrameInfo frameInfo)
        {
            ActiveRenderer.StartForwardRendering(frameInfo, VkAttachmentLoadOp.Clear);

            BlitEdgeTarget.Bind(frameInfo);
            GraphicsDevice.DeviceAPI.vkCmdDraw(frameInfo.CommandBuffer, 3, 1, 0, 0);

            ActiveRenderer.EndForwardRendering(frameInfo);
        }
#endif

        private void OutputBlending(RendererFrameInfo frameInfo)
        {
            if (InputManager.Instance.GetKeyUp(SDL3.SDL_Keycode.F8))
            {
                _smaaEnabled = !_smaaEnabled;
                Console.WriteLine($"SMAA ENABLED {_smaaEnabled}");
            }

            if (!_smaaEnabled) return;
            PostProcessingAttachment.BeginRenderingOnlyAttachment(frameInfo.CommandBuffer);
            NeighbourhoodBlending.Bind(frameInfo);
            GraphicsDevice.DeviceAPI.vkCmdDraw(frameInfo.CommandBuffer, 3, 1, 0, 0);
            GraphicsDevice.DeviceAPI.vkCmdEndRendering(frameInfo.CommandBuffer);
        }

        private void BlendWeightCalculation(RendererFrameInfo frameInfo)
        {
            BlendTarget.BeginRenderingOnlyAttachment(frameInfo.CommandBuffer);
            BlendWeightCalc.Bind(frameInfo);
            GraphicsDevice.DeviceAPI.vkCmdDraw(frameInfo.CommandBuffer, 3, 1, 0, 0);
            GraphicsDevice.DeviceAPI.vkCmdEndRendering(frameInfo.CommandBuffer);
        }

        private void EdgeDetectionPass(RendererFrameInfo frameInfo)
        {
            EdgeTarget.BeginRenderingOnlyAttachment(frameInfo.CommandBuffer);
            EdgeDetection.Bind(frameInfo);
            GraphicsDevice.DeviceAPI.vkCmdDraw(frameInfo.CommandBuffer, 3, 1, 0, 0);
            GraphicsDevice.DeviceAPI.vkCmdEndRendering(frameInfo.CommandBuffer);
        }
    }
}
