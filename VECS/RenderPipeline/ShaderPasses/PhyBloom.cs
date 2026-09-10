using System.Runtime.CompilerServices;
using VECS.LowLevel;
using Vortice.Vulkan;

namespace VECS
{
    public sealed class PhyBloom
    {
        private readonly static int SrcTextureId = "srcTexture".GetShaderPropertyId();
        private readonly static int DstTextureId = "dstTexture".GetShaderPropertyId();
        private readonly static int SrcLowTextureId = "srcLowTexture".GetShaderPropertyId();
        private readonly static int SrcHighTextureId = "srcHighTexture".GetShaderPropertyId();
        private readonly static int OutputImageSizeId = "constants.outputImageSize".GetShaderPropertyId();
        private readonly static int SrcResolutionId = "constants.srcResolution".GetShaderPropertyId();
        private readonly static int FilterRadiusSizeId = "constants.filterRadius".GetShaderPropertyId();
        private readonly static int BloomStrengthId ="constants.bloomStrength".GetShaderPropertyId();
        private readonly static int SrcBloomTextureId = "srcBloomTexture".GetShaderPropertyId();
        private readonly static int SrcMainTextureId = "srcMainTexture".GetShaderPropertyId();
        private readonly ComputePipeline _downSample;
        private readonly ComputePipeline _upSample;
        private readonly ComputeVariant _bloomMix;
        private readonly Material _bloomMixer;

        private readonly Texture2D _bloomDown;
        private readonly Texture2D _bloomUp;

        private VkImageView[] _mipDownViews;
        private VkImageView[] _mipUpViews;

        private readonly IRenderer _activeRenderer;

        public PhyBloom(IRenderer renderer)
        {
            _activeRenderer = renderer;

            _downSample = ComputePipeline.GetOrCreate("bloom_down_sample.comp");
            _upSample = ComputePipeline.GetOrCreate("bloom_up_sample.comp");
            _bloomMix = ComputePipeline.GetOrCreate("bloom_mix.comp").Default();
            var colourFormat = _activeRenderer.MainColourFormat;
            _bloomDown = new("PhyBloomDownTexture", 8, 8, colourFormat, VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst, VkSamplerAddressMode.ClampToEdge, true);
            _bloomUp = new("PhyBloomUpTexture", 8, 8, colourFormat, VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst, VkSamplerAddressMode.ClampToEdge, true);
            var config = GraphicsPipelineConfigInfo.DefaultPipelineConfigInfo([], []);
            config.rasterizationInfo.frontFace = VkFrontFace.Clockwise;
            config.rasterizationInfo.cullMode = VkCullModeFlags.Front;
            _bloomMixer = new GraphicsPipeline("BloomMixer", config, AssetDataBase<ShaderModule>.GetNamed("fullscreen.vert"), AssetDataBase<ShaderModule>.GetNamed("bloom_mixer.frag")).Default();
            _bloomMixer.SetFloat("constants.bloomStrength".GetShaderPropertyId(), 0.07f);
            Application.Instance.OnDestroy += CleanUpViews;

            RenderGraph.AddPass("PhyBloomDownSample", PassType.Compute, ["ForwardPass", "DeferredCompositePass", "TransaprentComposite", "SMAA_Output"], ["MainColourAttachment"], ["PhyBloomAttachment"],BloomDownSample);
            RenderGraph.AddPass("PhyBloomUpSample", PassType.Compute, ["PhyBloomDownSample"], ["MainColourAttachment"], ["PhyBloomAttachment"], BloomUpSample);
            RenderGraph.AddPass("PhyBloomMix", PassType.Compute, ["PhyBloomUpSample"], ["MainColourAttachment", "PhyBloomAttachment"], ["MainColourAttachment"], BloomMix);
        }

        private void CleanUpViews()
        {
            if (_mipDownViews != null)
            {
                for (int i = 0; i < _mipDownViews.Length; i++)
                {
                    TextureExtensions.EnqueueForDisposal(VkImage.Null, VmaAllocation.Null, _mipDownViews[i], VkSampler.Null);
                    TextureExtensions.EnqueueForDisposal(VkImage.Null, VmaAllocation.Null, _mipUpViews[i], VkSampler.Null);
                }
            }
        }

        public void RecreateRenderTargets()
        {
            var windowExtents = Application.MainWindow.WindowExtent;

            TextureLoader.CalculateMipLevelSize(windowExtents.width, windowExtents.height, 1, out var mipWidth, out var mipHeight);

            CleanUpViews();

            _bloomDown.Reinitialise(mipWidth, mipHeight);
            _bloomUp.Reinitialise(mipWidth, mipHeight);
            _mipDownViews = new VkImageView[7];
            _mipUpViews = new VkImageView[7];

            VkImageViewCreateInfo viewCreateInfoDown = _bloomDown.GetImageViewCreateInfo();
            VkImageViewCreateInfo viewCreateInfoUp = _bloomUp.GetImageViewCreateInfo();


            viewCreateInfoDown.subresourceRange.levelCount = 1;
            viewCreateInfoUp.subresourceRange.levelCount = 1;

            for (uint i = 0; i < 7; i++)
            {
                viewCreateInfoDown.subresourceRange.baseMipLevel = i;
                viewCreateInfoUp.subresourceRange.baseMipLevel = i;
                GraphicsDevice.DeviceAPI.vkCreateImageView(viewCreateInfoDown, out _mipDownViews[i]);
                GraphicsDevice.DeviceAPI.vkCreateImageView(viewCreateInfoUp, out _mipUpViews[i]);
                GraphicsDevice.SetObjectName(VkObjectType.ImageView, _mipDownViews[i].Handle, string.Format("PhyBloomDownMip_{0}", i));
                GraphicsDevice.SetObjectName(VkObjectType.ImageView, _mipUpViews[i].Handle, string.Format("PhyBloomUpMip_{0}", i));
            }
            SetImages();
        }
        private unsafe void SetImages()
        {
            int mipWidth, mipHeight;
            VkDescriptorImageInfo imageInfo = new()
            {
                imageLayout = VkImageLayout.General,
                sampler = _bloomDown.TextureSampler,

            };
            for (uint i = 0; i < 6; i++)
            {
                var downSampleVariant = _downSample.GetOrCreateVariant(i);
                TextureLoader.CalculateMipLevelSize(_bloomDown.Width, _bloomDown.Height, (int)i, out mipWidth, out mipHeight);

                imageInfo.imageView = _mipDownViews[i];

                downSampleVariant.SetVector2(SrcResolutionId, new(mipWidth, mipHeight));
                downSampleVariant.SetTexturesUnsafe(SrcTextureId, &imageInfo, 1);

                TextureLoader.CalculateMipLevelSize(_bloomDown.Width, _bloomDown.Height, (int)i + 1, out mipWidth, out mipHeight);
                imageInfo.imageView = _mipDownViews[i + 1];

                downSampleVariant.SetVector2(OutputImageSizeId, new(mipWidth, mipHeight));
                downSampleVariant.SetTexturesUnsafe(DstTextureId, &imageInfo, 1);
            }
            
            for (uint i = 0; i < 6; i++)
            {
                var upSampleVariant = _upSample.GetOrCreateVariant(i);

                TextureLoader.CalculateMipLevelSize(_bloomDown.Width, _bloomDown.Height, (int)i, out mipWidth, out mipHeight);
                upSampleVariant.SetVector2(OutputImageSizeId, new(mipWidth, mipHeight));
                upSampleVariant.SetFloat(FilterRadiusSizeId, 0.005f);

                imageInfo.imageView = _mipUpViews[i];
                upSampleVariant.SetTexturesUnsafe(DstTextureId, &imageInfo, 1);
                imageInfo.imageView =  _mipDownViews[i];
                upSampleVariant.SetTexturesUnsafe(SrcHighTextureId, &imageInfo, 1);

                imageInfo.imageView = i != 5 ? _mipUpViews[i+1] : _mipDownViews[i+1];
                upSampleVariant.SetTexturesUnsafe(SrcLowTextureId, &imageInfo, 1);
            }

            var windowExtents = Application.MainWindow.WindowExtent;
            _bloomMix.SetFloat(BloomStrengthId, 0.04f);
            _bloomMix.SetVector2(OutputImageSizeId, new(windowExtents.width, windowExtents.height));

            imageInfo.imageView = _mipUpViews[0];

            _bloomMixer.SetTexture(SrcBloomTextureId, _bloomUp);

            _bloomMix.SetTexturesUnsafe(SrcBloomTextureId,&imageInfo,1);
            var mainTarget = EngineTextures.TryGetTexture(ShaderProperties.MainColourAttachmentId);
            imageInfo = mainTarget.First.ImageInfo;
            _bloomMix.SetTexturesUnsafe(SrcMainTextureId, &imageInfo, 1);
            _bloomMix.SetTexturesUnsafe(DstTextureId, &imageInfo, 1);
            _bloomMixer.SetTexture(SrcMainTextureId,mainTarget);
        }
        private void BloomDownSample(RendererFrameInfo frameInfo)
        {
            SetImages();
            _bloomDown.SetImageLayoutAuto(frameInfo.CommandBuffer, VkImageLayout.TransferDstOptimal);
            ((DeferredRenderer)_activeRenderer).BlitFromMainColour(frameInfo.CommandBuffer, _bloomDown._vkImage, _bloomDown.Width, _bloomDown.Height, VkImageAspectFlags.Color);
            _bloomDown.SetImageLayoutAuto(frameInfo.CommandBuffer, VkImageLayout.General);

            var range = _bloomDown.GetSubresourceRange();
            range.levelCount = 1;

            for (uint i = 0; i < 6; i++)
            {
                unsafe
                {
                    VkMemoryBarrier2 memoryBarrier = new(VkPipelineStageFlags2.AllCommands, VkAccessFlags2.MemoryWrite | VkAccessFlags2.MemoryRead, VkPipelineStageFlags2.AllCommands, VkAccessFlags2.MemoryWrite | VkAccessFlags2.MemoryRead);

                    MemoryBarrierHelper.MemoryBarrier(frameInfo.CommandBuffer, memoryBarrier);
                }
                var variant = _downSample.GetOrCreateVariant(i);
                TextureLoader.CalculateMipLevelSize(_bloomDown.Width, _bloomDown.Height, (int)i, out int mipWidth,  out int mipHeight);
                variant.Dispatch(frameInfo.CommandBuffer,Presenter.FrameIndex,(uint)mipWidth,(uint)mipHeight);

                range.baseMipLevel = i;

                MemoryBarrierHelper.ImageMemoryBarrier(
                    frameInfo.CommandBuffer,
                    _bloomDown._vkImage,
                    range,
                    VkPipelineStageFlags2.ComputeShader,
                    VkAccessFlags2.ShaderRead,
                    VkPipelineStageFlags2.ComputeShader,
                    VkAccessFlags2.ShaderWrite,
                    VkImageLayout.General,
                    VkImageLayout.General,
                    Vulkan.VK_QUEUE_FAMILY_IGNORED,
                    Vulkan.VK_QUEUE_FAMILY_IGNORED);

                range.baseMipLevel = i + 1;

                MemoryBarrierHelper.ImageMemoryBarrier(
                    frameInfo.CommandBuffer,
                    _bloomDown._vkImage,
                    range,
                    VkPipelineStageFlags2.ComputeShader,
                    VkAccessFlags2.ShaderWrite,
                    VkPipelineStageFlags2.ComputeShader,
                    VkAccessFlags2.ShaderRead,
                    VkImageLayout.General,
                    VkImageLayout.General,
                    Vulkan.VK_QUEUE_FAMILY_IGNORED,
                    Vulkan.VK_QUEUE_FAMILY_IGNORED);
            }
            range = _bloomDown.GetSubresourceRange();
            MemoryBarrierHelper.ImageMemoryBarrier(
                frameInfo.CommandBuffer,
                _bloomDown._vkImage,
                range,
                VkPipelineStageFlags2.ComputeShader,
                VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead,
                VkPipelineStageFlags2.ComputeShader,
                VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead,
                VkImageLayout.General,
                VkImageLayout.General,
                Vulkan.VK_QUEUE_FAMILY_IGNORED,
                Vulkan.VK_QUEUE_FAMILY_IGNORED);
        }

        private void BloomUpSample(RendererFrameInfo frameInfo)
        {
            var range = _bloomDown.GetSubresourceRange();
            range.levelCount = 1;

            for (int i = 5; i >= 0; i--)
            {
                unsafe
                {
                    VkMemoryBarrier2 memoryBarrier = new(VkPipelineStageFlags2.AllCommands, VkAccessFlags2.MemoryWrite | VkAccessFlags2.MemoryRead, VkPipelineStageFlags2.AllCommands, VkAccessFlags2.MemoryWrite | VkAccessFlags2.MemoryRead);

                    MemoryBarrierHelper.MemoryBarrier(frameInfo.CommandBuffer, memoryBarrier);
                }
                var variant = _upSample.GetOrCreateVariant((uint)i);
                TextureLoader.CalculateMipLevelSize(_bloomDown.Width, _bloomDown.Height, (int)i, out int mipWidth, out int mipHeight);
                variant.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, (uint)mipWidth, (uint)mipHeight);

                range.baseMipLevel = (uint)i ;


                MemoryBarrierHelper.ImageMemoryBarrier(
                    frameInfo.CommandBuffer,
                    _bloomDown._vkImage,
                    range,
                    VkPipelineStageFlags2.ComputeShader,
                    VkAccessFlags2.ShaderRead,
                    VkPipelineStageFlags2.ComputeShader,
                    VkAccessFlags2.ShaderWrite,
                    VkImageLayout.General,
                    VkImageLayout.General,
                    Vulkan.VK_QUEUE_FAMILY_IGNORED,
                    Vulkan.VK_QUEUE_FAMILY_IGNORED);


                range.baseMipLevel = (uint)i;

                MemoryBarrierHelper.ImageMemoryBarrier(
                    frameInfo.CommandBuffer,
                    _bloomDown._vkImage,
                    range,
                    VkPipelineStageFlags2.ComputeShader,
                    VkAccessFlags2.ShaderWrite,
                    VkPipelineStageFlags2.ComputeShader,
                    VkAccessFlags2.ShaderRead,
                    VkImageLayout.General,
                    VkImageLayout.General,
                    Vulkan.VK_QUEUE_FAMILY_IGNORED,
                    Vulkan.VK_QUEUE_FAMILY_IGNORED);
            }

            _bloomDown.SetImageLayoutAuto(frameInfo.CommandBuffer, VkImageLayout.ShaderReadOnlyOptimal);
        }

        private void BloomMix(RendererFrameInfo frameInfo)
        {
            unsafe
            {
                VkMemoryBarrier2 memoryBarrier = new(VkPipelineStageFlags2.AllCommands, VkAccessFlags2.MemoryWrite | VkAccessFlags2.MemoryRead, VkPipelineStageFlags2.AllCommands, VkAccessFlags2.MemoryWrite | VkAccessFlags2.MemoryRead);

                MemoryBarrierHelper.MemoryBarrier(frameInfo.CommandBuffer, memoryBarrier);
            }
            _bloomUp.SetImageLayoutAuto(frameInfo.CommandBuffer, VkImageLayout.ShaderReadOnlyOptimal);
            //_bloomMix.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, GetGroupCount((uint)Screen.Width, 8), GetGroupCount((uint)Screen.Height, 8));

            var deferred = (DeferredRenderer)_activeRenderer;

            deferred.StartForwardRendering(frameInfo.CommandBuffer, VkAttachmentLoadOp.Clear, false);

            _bloomMixer.Bind(frameInfo);
            GraphicsDevice.DeviceAPI.vkCmdDraw(frameInfo.CommandBuffer, 3, 1, 0, 0);
            deferred.EndForwardRendering(frameInfo);

            unsafe
            {
                VkMemoryBarrier2 memoryBarrier = new(VkPipelineStageFlags2.AllCommands, VkAccessFlags2.MemoryWrite | VkAccessFlags2.MemoryRead, VkPipelineStageFlags2.AllCommands, VkAccessFlags2.MemoryWrite | VkAccessFlags2.MemoryRead);

                MemoryBarrierHelper.MemoryBarrier(frameInfo.CommandBuffer, memoryBarrier);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint GetGroupCount(uint threadCount, uint localSize)
        {
            return (threadCount + localSize - 1) / localSize;
        }
    }
}
