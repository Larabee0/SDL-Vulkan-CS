using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using VECS.LowLevel;
using Vortice.Vulkan;

namespace VECS
{
    public sealed class UnityPhyBloom
    {
        private readonly static int SrcTextureId = "srcTexture".GetShaderPropertyId();
        private readonly static int DstTextureId = "dstTexture".GetShaderPropertyId();

        private readonly static int SrcLowTextureId = "srcLowTexture".GetShaderPropertyId();
        private readonly static int SrcHighTextureId = "srcHighTexture".GetShaderPropertyId();

        private readonly static int SrcBloomTextureId = "srcBloomTexture".GetShaderPropertyId();
        private readonly static int SrcMainTextureId = "srcMainTexture".GetShaderPropertyId();

        private readonly static int SrcResolutionId = "constants.srcResolution".GetShaderPropertyId();
        private readonly static int OutputImageSizeId = "constants.outputImageSize".GetShaderPropertyId();
        private readonly static int BloomThresholdId = "constants.bloomThreshold".GetShaderPropertyId();
        private readonly static int BloomTintId = "constants.bloomTint".GetShaderPropertyId();
        private readonly static int BloomStrengthId = "constants.bloomStrength".GetShaderPropertyId();

        private readonly static int LowSizeId = "constants.lowSize".GetShaderPropertyId();
        private readonly static int HighSizeId = "constants.highSize".GetShaderPropertyId();
        private readonly static int ScatterId = "constants.scatter".GetShaderPropertyId();
        private readonly static int FilterRadiusSizeId = "constants.filterRadius".GetShaderPropertyId();

        private static readonly int inputTextureId = "inputTexture".GetShaderPropertyId();
        private readonly ComputeVariant _bloomPrefilter;
        private readonly ComputeVariant _bloomBlur;
        private readonly ComputePipeline _bloomDownSampleBlur;
        private readonly ComputePipeline _bloomUpSample;
        private readonly ComputeVariant _bloomUberPost;

        private readonly Material blit;
        private readonly Material _bloomMix;


        private Texture2D[] _bloomMipDown;
        private Texture2D[] _bloomMipUp;
        private readonly Texture2D _bloomFinalMipUp;

        private readonly Texture2D _bloomIntermediate;

        private readonly IRenderer _activeRenderer;

        private static bool Bloom_Enabled = true;
        private RenderTarget PostProcessingAttachment;

        public UnityPhyBloom(IRenderer renderer)
        {
            _activeRenderer = renderer;

            _bloomPrefilter = ComputePipeline.GetOrCreate("BloomPrefilter.comp").Default();
            _bloomBlur = ComputePipeline.GetOrCreate("BloomBlur.comp").Default();
            _bloomDownSampleBlur = ComputePipeline.GetOrCreate("BloomBlurDownSample.comp");
            //_bloomUpSample = ComputePipeline.GetOrCreate("bloom_up_sample.comp");
            _bloomUpSample = ComputePipeline.GetOrCreate("BloomUpSample.comp");
            _bloomUberPost = ComputePipeline.GetOrCreate("bloom_mix.comp").Default();

            blit = EnginePipes.Blit.Create("Bloom");
            var config = GraphicsPipelineConfigInfo.DefaultPipelineConfigInfo([], []);
            config.rasterizationInfo.frontFace = VkFrontFace.Clockwise;
            config.rasterizationInfo.cullMode = VkCullModeFlags.Front;
            _bloomMix = new GraphicsPipeline("BloomMixer", config, AssetDataBase<ShaderModule>.GetNamed( "fullscreen.vert"), AssetDataBase<ShaderModule>.GetNamed("bloom_mixer.frag")).Default();
            _bloomMix.SetFloat("constants.bloomStrength".GetShaderPropertyId(), 0.07f);
            var colourFormat = VkFormat.B10G11R11UfloatPack32; //_activeRenderer.ColourFormats[0];
            //_bloomDown = new("PhyBloomDownTexture", 8, 8, colourFormat, VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst, VkSamplerAddressMode.ClampToEdge,0,false,VkCompareOp.Never,VkSamplerMipmapMode.Nearest,VkBorderColor.FloatOpaqueBlack,VkFilter.Linear, true);
            _bloomFinalMipUp = new("BloomFinalMipUp", 8, 8, colourFormat, VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst, VkSamplerAddressMode.ClampToEdge, 0, false, VkCompareOp.Never, VkSamplerMipmapMode.Nearest, VkBorderColor.FloatOpaqueBlack, VkFilter.Linear, false);
            Application.Instance.OnDestroy += CleanUpViews;

            _bloomIntermediate = new("BloomIntermediate", 8, 8, colourFormat, VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst, VkSamplerAddressMode.ClampToEdge, 0, false, VkCompareOp.Never, VkSamplerMipmapMode.Nearest, VkBorderColor.FloatOpaqueBlack, VkFilter.Linear, false);

            RenderGraph.AddPass("PhyBloomDownSample", PassType.Compute, ["ForwardPass", "DeferredCompositePass", "TransaprentComposite", "SMAA_Output"], ["PostProcessingColourAttachment"], ["PhyBloomAttachment"], BloomDownSample);
            RenderGraph.AddPass("PhyBloomUpSample", PassType.Compute, ["PhyBloomDownSample"], ["MainColourAttachment"], ["PhyBloomAttachment"], BloomUpSample);
            RenderGraph.AddPass("PhyBloomMix", PassType.Compute, ["PhyBloomUpSample"], ["PostProcessingColourAttachment", "MainColourAttachment", "PhyBloomAttachment"], ["MainColourAttachment", "PostProcessingColourAttachment"], BloomMix);
        }

        public static void Bloom_Toggle_Input()
        {
            if (InputManager.Instance.GetKeyUp(SDL3.SDL_Keycode.B))
            {
                Bloom_Enabled = !Bloom_Enabled;

                Console.WriteLine("Bloom Enabled: {0}", Bloom_Enabled);
                if (Bloom_Enabled)
                {
                    RenderGraph.EnablePasses(["PhyBloomDownSample", "PhyBloomUpSample", "PhyBloomMix"]);
                }
                else
                {
                    RenderGraph.DisablePasses(["PhyBloomDownSample", "PhyBloomUpSample", "PhyBloomMix"]);
                }
            }
        }

        private void CleanUpViews()
        {
            if (_bloomMipDown != null)
            {
                for (int i = 0; i < _bloomMipDown.Length; i++)
                {
                    AssetDataBase<Texture2D>.Remove(_bloomMipDown[i]);
                    _bloomMipDown[i].Dispose();
                }
                for (int i = 0; i < _bloomMipUp.Length; i++)
                {
                    AssetDataBase<Texture2D>.Remove(_bloomMipUp[i]);
                    _bloomMipUp[i].Dispose();
                }
            }
        }

        public void RecreateRenderTargets()
        {
            var windowExtents = Application.MainWindow.WindowExtent;

            TextureLoader.CalculateMipLevelSize(windowExtents.width, windowExtents.height, 1, out var mipWidth, out var mipHeight);
            PostProcessingAttachment = RenderGraph.GetResource("PostProcessingColourAttachment");

            CleanUpViews();
            _bloomFinalMipUp.Reinitialise(mipWidth, mipHeight);
            _bloomIntermediate.Reinitialise((int)windowExtents.width, (int)windowExtents.height);
            var mipLevelCount = TextureExtensions.CalculateMipMapLevels(mipWidth, mipHeight) - 2;

            _bloomMipDown = new Texture2D[mipLevelCount];

            var colourFormat = VkFormat.B10G11R11UfloatPack32;//_activeRenderer.ColourFormats[0];
            for (int i = 0; i < mipLevelCount; i++)
            {
                TextureLoader.CalculateMipLevelSize(_bloomFinalMipUp.Width, _bloomFinalMipUp.Height, i, out mipWidth, out mipHeight);
                _bloomMipDown[i] = new(string.Format("BloomMipDown_{0}x{1}", mipWidth, mipHeight), mipWidth, mipHeight, colourFormat, VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst, VkSamplerAddressMode.ClampToEdge, 0, false, VkCompareOp.Never, VkSamplerMipmapMode.Nearest, VkBorderColor.FloatOpaqueBlack, VkFilter.Linear, false);
            }
            _bloomMipUp = new Texture2D[mipLevelCount];
            _bloomMipUp[0] = _bloomFinalMipUp;
            for (int i = 1; i < mipLevelCount; i++)
            {
                TextureLoader.CalculateMipLevelSize(_bloomFinalMipUp.Width, _bloomFinalMipUp.Height, i, out mipWidth, out mipHeight);
                _bloomMipUp[i] = new(string.Format("BloomMipUp_{0}x{1}", mipWidth, mipHeight), mipWidth, mipHeight, colourFormat, VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst, VkSamplerAddressMode.ClampToEdge, 0, false, VkCompareOp.Never, VkSamplerMipmapMode.Nearest, VkBorderColor.FloatOpaqueBlack, VkFilter.Linear, false);
            }

            SetImages();
        }
        private unsafe void SetImages()
        {
            if (!Presenter.NewSwapChain && Presenter.FrameCount > 20) return;
            var windowExtents = Application.MainWindow.WindowExtent;
            var mainTarget = EngineTextures.TryGetTexture(ShaderProperties.MainColourAttachmentId);
            VkDescriptorImageInfo imageInfo = mainTarget.First.ImageInfo;
            _bloomUberPost.SetVector2(OutputImageSizeId, new(windowExtents.width, windowExtents.height));
            Vector4 bloomThreshold = new(0.0f, -1.0e-5f, 0.00002f, 25000.0f);
            _bloomPrefilter.SetVector2(SrcResolutionId, new(windowExtents.width, windowExtents.height));
            _bloomPrefilter.SetVector2(OutputImageSizeId, new(_bloomFinalMipUp.Width, _bloomFinalMipUp.Height));
            _bloomPrefilter.SetVector4(BloomThresholdId, bloomThreshold);

            imageInfo = PostProcessingAttachment.Target.ImageInfo;
            _bloomPrefilter.SetTexturesUnsafe(SrcTextureId, &imageInfo, 1);
            _bloomUberPost.SetTexturesUnsafe(DstTextureId, &imageInfo, 1);
            imageInfo = _bloomIntermediate.ImageInfo;
            _bloomUberPost.SetTexturesUnsafe(SrcMainTextureId, &imageInfo, 1);
            imageInfo = _bloomFinalMipUp.ImageInfo;
            _bloomPrefilter.SetTexturesUnsafe(DstTextureId, &imageInfo, 1);

            _bloomBlur.SetVector2(OutputImageSizeId, new(_bloomMipDown[0].Width, _bloomMipDown[0].Height));
            _bloomBlur.SetTexturesUnsafe(SrcTextureId, &imageInfo, 1);
            imageInfo = _bloomMipDown[0].ImageInfo;
            _bloomBlur.SetTexturesUnsafe(DstTextureId, &imageInfo, 1);

            for (uint i = 0; i < _bloomMipDown.Length - 1; i++)
            {
                var downSampleVariant = _bloomDownSampleBlur.GetOrCreateVariant(i);

                imageInfo = _bloomMipDown[i].ImageInfo;

                downSampleVariant.SetTexturesUnsafe(SrcTextureId, &imageInfo, 1);

                imageInfo = _bloomMipDown[i+1].ImageInfo;

                downSampleVariant.SetVector2(OutputImageSizeId, new(_bloomMipDown[i + 1].Width, _bloomMipDown[i + 1].Height));
                downSampleVariant.SetTexturesUnsafe(DstTextureId, &imageInfo, 1);
            }

            ComputeVariant[] upSampleVariants = new ComputeVariant[_bloomMipDown.Length + 2];

            for (uint i = 0; i < _bloomMipDown.Length+2; i++)
            {
                upSampleVariants[i] = _bloomUpSample.GetOrCreateVariant(i);
                upSampleVariants[i].SetFloat(ScatterId, 0.7f);
            }

            var upSampleVariant = upSampleVariants[(uint)_bloomMipDown.Length - 1];
            imageInfo = _bloomMipDown[^2].ImageInfo;
            upSampleVariant.SetTexturesUnsafe(SrcHighTextureId, &imageInfo, 1);
            upSampleVariant.SetVector2(HighSizeId, new(_bloomMipDown[^2].Width, _bloomMipDown[^2].Height));

            imageInfo = _bloomMipDown[^1].ImageInfo;
            upSampleVariant.SetTexturesUnsafe(SrcLowTextureId, &imageInfo, 1);
            upSampleVariant.SetVector2(LowSizeId, new(_bloomMipUp[^1].Width, _bloomMipUp[^1].Height));

            imageInfo = _bloomMipUp[^2].ImageInfo;
            upSampleVariant.SetTexturesUnsafe(DstTextureId, &imageInfo, 1);
            upSampleVariant.SetFloat(ScatterId, 0.7f);
            upSampleVariant.SetFloat(FilterRadiusSizeId, 0.005f);

            for (int i = _bloomMipDown.Length-3; i >= 0; i--)
            {
                upSampleVariant = upSampleVariants[i];
                imageInfo = _bloomMipDown[i].ImageInfo;
                upSampleVariant.SetTexturesUnsafe(SrcHighTextureId, &imageInfo, 1);
                upSampleVariant.SetVector2(HighSizeId, new(_bloomMipDown[i].Width, _bloomMipDown[i].Height));

                imageInfo = _bloomMipUp[i+1].ImageInfo;
                upSampleVariant.SetTexturesUnsafe(SrcLowTextureId, &imageInfo, 1);
                upSampleVariant.SetVector2(LowSizeId, new(_bloomMipUp[i + 1].Width, _bloomMipUp[i + 1].Height));


                imageInfo = _bloomMipUp[i].ImageInfo;
                upSampleVariant.SetTexturesUnsafe(DstTextureId, &imageInfo, 1);

                upSampleVariant.SetFloat(ScatterId, 0.7f);
                upSampleVariant.SetFloat(FilterRadiusSizeId, 0.005f);
            }

            // fine
            imageInfo = _bloomFinalMipUp.ImageInfo;
            _bloomUberPost.SetTexturesUnsafe(SrcBloomTextureId, &imageInfo, 1);
            _bloomUberPost.SetVector4(BloomThresholdId, bloomThreshold);
            _bloomUberPost.SetVector4(BloomTintId, new(1, 1, 1, 1));
            _bloomUberPost.SetFloat(BloomStrengthId, 0.17177f);

            _bloomMix.SetTexture(SrcBloomTextureId, _bloomFinalMipUp);
            _bloomMix.SetTexture(SrcMainTextureId, _bloomIntermediate);
            blit.SetTexture(inputTextureId, _bloomIntermediate);
        }
         
        private static VkImageMemoryBarrier2 GetImageBarrier(VkAccessFlags2 srcAccess, VkAccessFlags2 dstAccess, Texture2D texture, int mipMap)
        {
            VkImageMemoryBarrier2 imageMemoryBarrier = new()
            {
                dstStageMask = VkPipelineStageFlags2.ComputeShader,
                srcStageMask = VkPipelineStageFlags2.ComputeShader,
                oldLayout = texture.ImageLayout,
                newLayout = VkImageLayout.General,
                dstQueueFamilyIndex = Vulkan.VK_QUEUE_FAMILY_IGNORED,
                srcQueueFamilyIndex = Vulkan.VK_QUEUE_FAMILY_IGNORED,
                image = texture._vkImage,
                subresourceRange = texture.GetSubresourceRange(),
                srcAccessMask = srcAccess,
                dstAccessMask = dstAccess,
            };
            imageMemoryBarrier.subresourceRange.levelCount = 1;
            imageMemoryBarrier.subresourceRange.baseMipLevel = (uint)mipMap;
            texture.SetImageLayoutSilent(VkImageLayout.General);
            return imageMemoryBarrier;
        }

        private static unsafe void MemoryBarrier(VkCommandBuffer commandBuffer)
        {
            VkMemoryBarrier2 memoryBarrier = new(VkPipelineStageFlags2.AllCommands, VkAccessFlags2.MemoryWrite | VkAccessFlags2.MemoryRead, VkPipelineStageFlags2.AllCommands, VkAccessFlags2.MemoryWrite | VkAccessFlags2.MemoryRead);

            MemoryBarrierHelper.MemoryBarrier(commandBuffer, memoryBarrier);
        }

        private unsafe void BloomDownSample(RendererFrameInfo frameInfo)
        {
            SetImages();
            var deferred = (DeferredRenderer)_activeRenderer;

            _bloomIntermediate.SetImageLayoutAuto(frameInfo.CommandBuffer, VkImageLayout.TransferDstOptimal);

            deferred.BlitFromPostProcessingColour(frameInfo.CommandBuffer, _bloomIntermediate._vkImage, _bloomIntermediate.Width, _bloomIntermediate.Height, VkImageAspectFlags.Color);
            _bloomIntermediate.SetImageLayoutAuto(frameInfo.CommandBuffer, VkImageLayout.ShaderReadOnlyOptimal);
            MemoryBarrier(frameInfo.CommandBuffer);
            _bloomPrefilter.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, GetGroupCount((uint)Screen.Width, 8), GetGroupCount((uint)Screen.Height, 8));
            VkImageMemoryBarrier2* barriers = stackalloc VkImageMemoryBarrier2[2];
            barriers[0] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomFinalMipUp, 0);
            barriers[0].newLayout = VkImageLayout.ShaderReadOnlyOptimal;
            _bloomFinalMipUp.SetImageLayoutSilent(VkImageLayout.ShaderReadOnlyOptimal);

            MemoryBarrier(frameInfo.CommandBuffer);

            MemoryBarrierHelper.ImageMemoryBarrier(frameInfo.CommandBuffer, barriers, 1);

            MemoryBarrier(frameInfo.CommandBuffer);
            _bloomBlur.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, GetGroupCount((uint)_bloomMipDown[0].Width, 8), GetGroupCount((uint)_bloomMipDown[0].Height, 8));
            MemoryBarrier(frameInfo.CommandBuffer);
            barriers[0] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomFinalMipUp, 0);
            barriers[1] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomMipDown[0], 0);
            barriers[1].newLayout = VkImageLayout.ShaderReadOnlyOptimal;
            _bloomMipDown[0].SetImageLayoutSilent(VkImageLayout.ShaderReadOnlyOptimal);
            MemoryBarrierHelper.ImageMemoryBarrier(frameInfo.CommandBuffer, barriers, 2);

            for (uint i = 0; i < _bloomMipDown.Length-1; i++)
            {
                var variant = _bloomDownSampleBlur.GetOrCreateVariant(i);
                MemoryBarrier(frameInfo.CommandBuffer);
                variant.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, GetGroupCount((uint)_bloomMipDown[i].Width, 8), GetGroupCount((uint)_bloomMipDown[i].Height, 8));
                MemoryBarrier(frameInfo.CommandBuffer);
                barriers[0] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomMipDown[i], 0);
                barriers[1] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomMipDown[i+1],0);
                barriers[1].newLayout = VkImageLayout.ShaderReadOnlyOptimal;
                _bloomMipDown[i + 1].SetImageLayoutSilent(VkImageLayout.ShaderReadOnlyOptimal);
                MemoryBarrierHelper.ImageMemoryBarrier(frameInfo.CommandBuffer, barriers, 2);
            }
        }

        private unsafe void BloomUpSample(RendererFrameInfo frameInfo)
        {
            VkImageMemoryBarrier2* barriers = stackalloc VkImageMemoryBarrier2[3];
            var variant = _bloomUpSample.GetOrCreateVariant((uint)_bloomMipDown.Length - 1);
            MemoryBarrier(frameInfo.CommandBuffer);
            variant.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, GetGroupCount((uint)_bloomMipDown[^2].Width, 8), GetGroupCount((uint)_bloomMipDown[^2].Height, 8));
            MemoryBarrier(frameInfo.CommandBuffer);
            barriers[0] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomMipDown[^1], 0);
            barriers[1] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomMipDown[^2], 0);
            barriers[2] = GetImageBarrier(VkAccessFlags2.ShaderWrite, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomMipUp[^2], 0);
            barriers[2].newLayout = VkImageLayout.ShaderReadOnlyOptimal;
            _bloomMipUp[^2].SetImageLayoutSilent(VkImageLayout.ShaderReadOnlyOptimal);
            MemoryBarrierHelper.ImageMemoryBarrier(frameInfo.CommandBuffer, barriers, 3);

            for (int i = _bloomMipUp.Length-3; i >= 0; i--)
            {
                variant = _bloomUpSample.GetOrCreateVariant((uint)i);
                MemoryBarrier(frameInfo.CommandBuffer);
                variant.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, GetGroupCount((uint)_bloomMipDown[i].Width, 8), GetGroupCount((uint)_bloomMipDown[i].Height, 8));
                MemoryBarrier(frameInfo.CommandBuffer);
                barriers[0] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomMipUp[i + 1], 0);
                barriers[1] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomMipDown[i], 0);
                barriers[2] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomMipUp[i], 0);
                barriers[2].newLayout = VkImageLayout.ShaderReadOnlyOptimal;
                _bloomMipUp[i].SetImageLayoutSilent(VkImageLayout.ShaderReadOnlyOptimal);
                MemoryBarrierHelper.ImageMemoryBarrier(frameInfo.CommandBuffer, barriers, 3);
            }

        }

        private void BloomMix(RendererFrameInfo frameInfo)
        {
            _bloomFinalMipUp.SetImageLayoutAuto(frameInfo.CommandBuffer, VkImageLayout.ShaderReadOnlyOptimal);

            MemoryBarrier(frameInfo.CommandBuffer);
            _bloomUberPost.Dispatch(frameInfo.CommandBuffer,Presenter.FrameIndex,GetGroupCount((uint)Screen.Width,8), GetGroupCount((uint)Screen.Height, 8));
            MemoryBarrier(frameInfo.CommandBuffer);

            // var deferred = (DeferredRenderer)_activeRenderer;
            // deferred.StartForwardRendering(frameInfo.CommandBuffer, VkAttachmentLoadOp.Clear,true);
            // 
            // blit.Bind(frameInfo);
            // GraphicsDevice.DeviceAPI.vkCmdDraw(frameInfo.CommandBuffer,3,1,0,0);
            // deferred.EndForwardRendering(frameInfo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint GetGroupCount(uint threadCount, uint localSize)
        {
            return (threadCount + localSize - 1) / localSize;
        }
    }
}
