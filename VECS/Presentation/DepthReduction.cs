using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VECS.LowLevel;
using Vortice.Vulkan;

namespace VECS
{
    public static class DepthReduction
    {
        private static readonly int OutImagePropertyId = "outImage".GetShaderPropertyId();
        private static readonly int InImagePropertyId = "inImage".GetShaderPropertyId();

        private static uint _depthPyramidWidth;
        private static uint _depthPyramidHeight;
        private static ComputeVariant _depthReduceShader;
        private static Texture2D _depthPryamid;
        public static Texture2D DepthPryamid => _depthPryamid;

        private static VkImageView[] _additionalViews;

        private static unsafe VkDescriptorImageInfo* _srcImages;
        private static unsafe VkDescriptorImageInfo* _dstImages;

        public static void Init()
        {

            _depthReduceShader = ComputePipeline.GetOrCreate("depth_reduce.comp").Default();
            Presenter.OnSwapChainRecreation += RecreateImage;
            Application.Instance.OnDestroy += DestroyResources;
        }

        private unsafe static void DestroyResources()
        {
            _depthPryamid.Dispose();
            for (int i = 0; i < _additionalViews.Length; i++)
            {
                GraphicsDevice.DeviceAPI.vkDestroyImageView(_additionalViews[i]);
            }
            _depthReduceShader.Dispose();
            NativeMemory.Free(_srcImages);
            NativeMemory.Free(_dstImages);
            _srcImages = null;
            _dstImages = null;
        }

        private unsafe static void RecreateImage()
        {
            var windowExtent = Application.MainWindow.WindowExtent;
            _depthPyramidWidth = PreviousPow2(windowExtent.width);
            _depthPyramidHeight = PreviousPow2(windowExtent.height);
            VkFormat format = VkFormat.R32Sfloat;

            if (_depthPryamid == null)
            {
                _depthPryamid = new Texture2D(
                        string.Format("DepthPryamid {0}", Presenter.FrameCount),
                        (int)_depthPyramidWidth,
                        (int)_depthPyramidHeight,
                        format,
                        VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst | VkImageUsageFlags.TransferSrc,
                        VkSamplerAddressMode.ClampToEdge, 0, false, VkCompareOp.Never, VkSamplerMipmapMode.Nearest, VkBorderColor.FloatTransparentBlack,  VkFilter.Nearest
                );
    }
            else
            {
                for (int i = 0; i < _additionalViews.Length; i++)
                {
                    TextureExtensions.EnqueueForDisposal(VkImage.Null, VmaAllocation.Null, _additionalViews[i], VkSampler.Null);
                }

                _depthPryamid.Reinitialise((int)_depthPyramidWidth, (int)_depthPyramidHeight);
                 NativeMemory.Free(_srcImages);
                 NativeMemory.Free(_dstImages);
                _srcImages = null;
                _dstImages = null;
            }

            _srcImages = (VkDescriptorImageInfo*)NativeMemory.Alloc((uint)sizeof(VkDescriptorImageInfo) * _depthPryamid.MipMapCount);
            _dstImages = (VkDescriptorImageInfo*)NativeMemory.Alloc((uint)sizeof(VkDescriptorImageInfo) * _depthPryamid.MipMapCount);

            _additionalViews = new VkImageView[_depthPryamid.MipMapCount];

            for (uint i = 0; i < _additionalViews.Length; i++)
            {
                var createInfo = _depthPryamid.GetImageViewCreateInfo();
                createInfo.subresourceRange.levelCount = 1;
                createInfo.subresourceRange.baseMipLevel = i;
                GraphicsDevice.DeviceAPI.vkCreateImageView(createInfo, out _additionalViews[i]);
                GraphicsDevice.SetObjectName(VkObjectType.ImageView, _additionalViews[i].Handle,string.Format("DepthReductionAdd_{0}",i));
                _dstImages[i] = new VkDescriptorImageInfo()
                {
                    imageLayout = VkImageLayout.General,
                    imageView = _additionalViews[i],
                    sampler = _depthPryamid.TextureSampler
                };

                if (i > 0)
                {
                    _srcImages[i] = new VkDescriptorImageInfo()
                    {
                        imageLayout = VkImageLayout.General,
                        imageView = _additionalViews[i-1],
                        sampler = _depthPryamid.TextureSampler
                    };
                }
            }
        }

        public static unsafe void ClearPyramid(RendererFrameInfo frameInfo)
        {
            GraphicsDevice.BeginLabelCmd(frameInfo.CommandBuffer, "Clear Depth Pyramid");
            VkClearColorValue clearDepthStencilValue = new(0, 0, 0, 0);
            VkImageSubresourceRange subresourceRange = _depthPryamid.GetSubresourceRange();

            _depthPryamid.SetImageLayoutAuto(frameInfo.CommandBuffer, VkImageLayout.General);

            GraphicsDevice.DeviceAPI.vkCmdClearColorImage(frameInfo.CommandBuffer, _depthPryamid._vkImage, _depthPryamid.ImageLayout, &clearDepthStencilValue, 1, &subresourceRange);

            _depthPryamid.SetImageLayoutAuto(frameInfo.CommandBuffer, VkImageLayout.General);
            GraphicsDevice.EndLabelCmd(frameInfo.CommandBuffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReduceDepth(RendererFrameInfo frameInfo)
        {
            GraphicsDevice.BeginLabelCmd(frameInfo.CommandBuffer, "Depth Reduction");
            ComputeShaderTransfer(frameInfo);
            GraphicsDevice.EndLabelCmd(frameInfo.CommandBuffer);
        }

        private static unsafe void ComputeShaderTransfer(RendererFrameInfo frameInfo)
        {
            if (!frameInfo.CullData.cullMode.HasFlag(CullModeFlags.Depth)) return;

            var depthTexture = EngineTextures.TryGetTexture(ShaderProperties.MainDepthAttachmentId).First;
            

            VkImageMemoryBarrier2 depthReadBarrier = new()
            {
                srcAccessMask = VkAccessFlags2.DepthStencilAttachmentWrite,
                dstAccessMask = VkAccessFlags2.ShaderRead,
                oldLayout = depthTexture.ImageLayout,
                newLayout = VkImageLayout.ShaderReadOnlyOptimal,
                image = depthTexture._vkImage,
                subresourceRange = depthTexture.GetSubresourceRange(),
                srcStageMask = VkPipelineStageFlags2.LateFragmentTests,
                dstStageMask = VkPipelineStageFlags2.ComputeShader
            };

            VkDependencyInfo dependencyInfo = new()
            {
                dependencyFlags = VkDependencyFlags.ByRegion,
                imageMemoryBarrierCount = 1,
                pImageMemoryBarriers = &depthReadBarrier
            };

            GraphicsDevice.DeviceAPI.vkCmdPipelineBarrier2(frameInfo.CommandBuffer, &dependencyInfo);

            depthReadBarrier = new()
            {
                srcAccessMask = VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead,
                dstAccessMask = VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead,
                oldLayout = VkImageLayout.General,
                newLayout = VkImageLayout.General,
                image = _depthPryamid._vkImage,
                subresourceRange = _depthPryamid.GetSubresourceRange(),
                srcStageMask = VkPipelineStageFlags2.ComputeShader,
                dstStageMask = VkPipelineStageFlags2.ComputeShader
            };
            dependencyInfo.pImageMemoryBarriers = &depthReadBarrier;

            _srcImages[0] = new()
            {
                sampler = _depthPryamid.TextureSampler,
                imageView = depthTexture._imageView,
                imageLayout = VkImageLayout.ShaderReadOnlyOptimal
            };

            _depthReduceShader.SetTexturesUnsafe(InImagePropertyId, _srcImages,_depthPryamid.MipMapCount);
            _depthReduceShader.SetTexturesUnsafe(OutImagePropertyId, _dstImages, _depthPryamid.MipMapCount);

            for (int i = 0; i < _depthPryamid.MipMapCount; i++)
            {
                uint x = Math.Max(1, _depthPryamid.ImageExtent.width >> i);
                uint y = Math.Max(1, _depthPryamid.ImageExtent.height >> i);

                _depthReduceShader.PushConstantsHandler.SetPushConstantVector2("imageSize", 0, new(x, y));
                _depthReduceShader.PushConstantsHandler.SetPushConstantInt("srcIndex", 0, i);
                _depthReduceShader.PushConstantsHandler.SetPushConstantInt("dstIndex", 0, i);
                _depthReduceShader.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, x, y);

                GraphicsDevice.DeviceAPI.vkCmdPipelineBarrier2(frameInfo.CommandBuffer, &dependencyInfo);
            }

            VkImageMemoryBarrier2 depthTextureBarrier = new()
            {
                srcAccessMask = VkAccessFlags2.ShaderRead,
                dstAccessMask = VkAccessFlags2.DepthStencilAttachmentRead | VkAccessFlags2.DepthStencilAttachmentWrite,
                oldLayout = VkImageLayout.ShaderReadOnlyOptimal,
                newLayout = depthTexture.ImageLayout,
                image = depthTexture._vkImage,
                subresourceRange = depthTexture.GetSubresourceRange(),
                srcStageMask = VkPipelineStageFlags2.ComputeShader,
                dstStageMask = VkPipelineStageFlags2.EarlyFragmentTests,
            };

            VkDependencyInfo depthDependencyInfo = new()
            {
                pImageMemoryBarriers = &depthTextureBarrier,
                imageMemoryBarrierCount = 1,
                dependencyFlags = VkDependencyFlags.ByRegion
            };
            GraphicsDevice.DeviceAPI.vkCmdPipelineBarrier2(frameInfo.CommandBuffer, &depthDependencyInfo);
            depthTexture._imageLayout = VkImageLayout.DepthStencilAttachmentOptimal;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint PreviousPow2(uint v)
        {
            uint r = 1;
            while (r * 2 < v)
            {
                r *= 2;
            }
            return r;
        }

    }
}
