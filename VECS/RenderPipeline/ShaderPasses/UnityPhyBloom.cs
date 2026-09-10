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

        private static readonly Vector4 BloomThreshold = new(0.0f, -1.0e-5f, 0.00002f, 25000.0f);
        private static readonly Vector4 BloomTint = new(1, 1, 1, 1);
        private static readonly float BloomStrength = 0.17177f;
        private static readonly float BloomScatter = 0.7f;

        private readonly ComputeVariant _bloomPrefilter;
        private readonly ComputeVariant _bloomBlur;
        private readonly ComputePipeline _bloomDownSampleBlur;
        private readonly ComputePipeline _bloomUpSample;
        private readonly ComputeVariant _bloomUberPost;

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

            _bloomUberPost = ComputePipeline.GetOrCreate("UberPost.comp").Default();
            _bloomPrefilter = ComputePipeline.GetOrCreate("BloomPrefilter.comp").Default();
            _bloomBlur = ComputePipeline.GetOrCreate("BloomBlur.comp").Default();

            _bloomDownSampleBlur = ComputePipeline.GetOrCreate("BloomBlurDownSample.comp");
            _bloomUpSample = ComputePipeline.GetOrCreate("BloomUpSample.comp");

            var colourFormat = _activeRenderer.PostProcessingColourFormat;

            _bloomFinalMipUp = new("BloomFinalMipUp", 8, 8, colourFormat, VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst, VkSamplerAddressMode.ClampToEdge, 0, false, VkCompareOp.Never, VkSamplerMipmapMode.Nearest, VkBorderColor.FloatOpaqueBlack, VkFilter.Linear, false);
            
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

        private void DisposeBloomMips()
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

            DisposeBloomMips();
            _bloomFinalMipUp.Reinitialise(mipWidth, mipHeight);
            _bloomIntermediate.Reinitialise((int)windowExtents.width, (int)windowExtents.height);
            var mipLevelCount = TextureExtensions.CalculateMipMapLevels(mipWidth, mipHeight) - 2;

            _bloomMipDown = new Texture2D[mipLevelCount];

            var colourFormat = _activeRenderer.PostProcessingColourFormat;
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

            SetBloomPreFilterVariant(windowExtents);
            SetUberPostVariant(windowExtents);

            SetBloomDownSampleVariants();
            SetBloomUpsamplerVariants();
        }

        private void SetBloomDownSampleVariants()
        {
            _bloomBlur.SetVector2(OutputImageSizeId, new(_bloomMipDown[0].Width, _bloomMipDown[0].Height));
            _bloomBlur.SetTexture(DstTextureId, _bloomMipDown[0]);
            _bloomBlur.SetTexture(SrcTextureId, _bloomFinalMipUp);

            for (uint i = 0; i < _bloomMipDown.Length - 1; i++)
            {
                var downSampleVariant = _bloomDownSampleBlur.GetOrCreateVariant(i);

                downSampleVariant.SetTexture(SrcTextureId, _bloomMipDown[i]);

                downSampleVariant.SetVector4(OutputImageSizeId, new(_bloomMipDown[i + 1].Width, _bloomMipDown[i + 1].Height, 1.0f / _bloomMipDown[i + 1].Width, 1.0f / _bloomMipDown[i + 1].Height));
                downSampleVariant.SetTexture(DstTextureId, _bloomMipDown[i + 1]);
            }
        }

        private void SetBloomUpsamplerVariants()
        {
            ComputeVariant[] upSampleVariants = new ComputeVariant[_bloomMipDown.Length-1];

            for (uint i = 0; i < _bloomMipDown.Length-1; i++)
            {
                upSampleVariants[i] = _bloomUpSample.GetOrCreateVariant(i);
            }
            var variant = upSampleVariants[^1];
            SetUpSampleVariant(variant, _bloomMipDown[^1], _bloomMipDown[^2], _bloomMipUp[^2], BloomScatter);

            for (int i = _bloomMipDown.Length - 3; i >= 0; i--)
            {
                SetUpSampleVariant(upSampleVariants[i], _bloomMipUp[i + 1], _bloomMipDown[i], _bloomMipUp[i], BloomScatter);
            }
        }

        private void SetBloomPreFilterVariant(VkExtent2D windowExtents)
        {
            _bloomPrefilter.SetVector2(SrcResolutionId, new(windowExtents.width, windowExtents.height));
            _bloomPrefilter.SetVector4(OutputImageSizeId, new Vector4(_bloomFinalMipUp.Width, _bloomFinalMipUp.Height, 1.0f / _bloomFinalMipUp.Width, 1.0f / _bloomFinalMipUp.Height));
            _bloomPrefilter.SetVector4(BloomThresholdId, BloomThreshold);

            _bloomPrefilter.SetTexture(SrcTextureId, PostProcessingAttachment.Target);
            _bloomPrefilter.SetTexture(DstTextureId, _bloomFinalMipUp);
        }

        private void SetUberPostVariant(VkExtent2D windowExtents)
        {
            _bloomUberPost.SetVector4(OutputImageSizeId, new(windowExtents.width, windowExtents.height, 1.0f / windowExtents.width, 1.0f / windowExtents.height));
            _bloomUberPost.SetVector4(BloomThresholdId, BloomThreshold);
            _bloomUberPost.SetVector4(BloomTintId, BloomTint);
            _bloomUberPost.SetFloat(BloomStrengthId, BloomStrength);

            _bloomUberPost.SetTexture(DstTextureId, PostProcessingAttachment.Target);
            _bloomUberPost.SetTexture(SrcMainTextureId, _bloomIntermediate);
            _bloomUberPost.SetTexture(SrcBloomTextureId, _bloomFinalMipUp);
        }

        private static void SetUpSampleVariant(ComputeVariant upSampleVariant, Texture2D lowTexture, Texture2D highTexture, Texture2D outputTexture, float scatter)
        {
            upSampleVariant.SetVector4(HighSizeId, new(highTexture.Width, highTexture.Height, 1.0f / highTexture.Width, 1.0f / highTexture.Height));
            upSampleVariant.SetVector4(LowSizeId, new(lowTexture.Width, lowTexture.Height, 1.0f / lowTexture.Width, 1.0f / lowTexture.Height));
            upSampleVariant.SetFloat(ScatterId, scatter);

            upSampleVariant.SetTexture(SrcHighTextureId, highTexture);
            upSampleVariant.SetTexture(SrcLowTextureId, lowTexture);
            upSampleVariant.SetTexture(DstTextureId, outputTexture);
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

        private unsafe void BloomDownSample(RendererFrameInfo frameInfo)
        {
            var deferred = (DeferredRenderer)_activeRenderer;

            VkImageMemoryBarrier2* barriers = stackalloc VkImageMemoryBarrier2[2];
            barriers[0] = _bloomIntermediate.GetImageLayoutBarrierAuto(VkImageLayout.TransferDstOptimal);
            barriers[1] = PostProcessingAttachment.Target.GetImageLayoutBarrierAuto(VkImageLayout.TransferSrcOptimal);

            MemoryBarrierHelper.ImageMemoryBarrier(frameInfo.CommandBuffer, barriers,2);

            PostProcessingAttachment.Target.SetImageLayoutSilent(VkImageLayout.TransferSrcOptimal);
            _bloomIntermediate.SetImageLayoutSilent(VkImageLayout.TransferDstOptimal);

            TextureExtensions.BlitGeneric(frameInfo.CommandBuffer, VkFilter.Linear, PostProcessingAttachment.GetBlitCmd(_bloomIntermediate.Width, _bloomIntermediate.Height, VkImageAspectFlags.Color), PostProcessingAttachment.VkImage, VkImageLayout.TransferSrcOptimal, _bloomIntermediate._vkImage, VkImageLayout.TransferDstOptimal);

            barriers[0] = _bloomIntermediate.GetImageLayoutBarrierAuto(VkImageLayout.ShaderReadOnlyOptimal);

            MemoryBarrierHelper.ImageMemoryBarrier(frameInfo.CommandBuffer, barriers, 1);
            _bloomIntermediate.SetImageLayoutSilent(VkImageLayout.ShaderReadOnlyOptimal);
            _bloomPrefilter.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, (uint)Screen.Width, (uint)Screen.Height);
            barriers[0] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomFinalMipUp, 0);
            barriers[0].newLayout = VkImageLayout.ShaderReadOnlyOptimal;
            _bloomFinalMipUp.SetImageLayoutSilent(VkImageLayout.ShaderReadOnlyOptimal);

            MemoryBarrierHelper.ImageMemoryBarrier(frameInfo.CommandBuffer, barriers, 1);

            _bloomBlur.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, (uint)_bloomMipDown[0].Width, (uint)_bloomMipDown[0].Height);
            barriers[0] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomFinalMipUp, 0);
            barriers[1] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomMipDown[0], 0);
            barriers[1].newLayout = VkImageLayout.ShaderReadOnlyOptimal;
            _bloomMipDown[0].SetImageLayoutSilent(VkImageLayout.ShaderReadOnlyOptimal);
            MemoryBarrierHelper.ImageMemoryBarrier(frameInfo.CommandBuffer, barriers, 2);

            for (uint i = 0; i < _bloomMipDown.Length-1; i++)
            {
                var variant = _bloomDownSampleBlur.GetOrCreateVariant(i);
                variant.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, (uint)_bloomMipDown[i].Width, (uint)_bloomMipDown[i].Height);
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
            var variant = _bloomUpSample.GetOrCreateVariant((uint)_bloomMipDown.Length - 2);
            variant.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, (uint)_bloomMipDown[^2].Width, (uint)_bloomMipDown[^2].Height);
            barriers[0] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomMipDown[^1], 0);
            barriers[1] = GetImageBarrier(VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomMipDown[^2], 0);
            barriers[2] = GetImageBarrier(VkAccessFlags2.ShaderWrite, VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderRead, _bloomMipUp[^2], 0);
            barriers[2].newLayout = VkImageLayout.ShaderReadOnlyOptimal;
            _bloomMipUp[^2].SetImageLayoutSilent(VkImageLayout.ShaderReadOnlyOptimal);
            MemoryBarrierHelper.ImageMemoryBarrier(frameInfo.CommandBuffer, barriers, 3);

            for (int i = _bloomMipUp.Length-3; i >= 0; i--)
            {
                variant = _bloomUpSample.GetOrCreateVariant((uint)i);
                variant.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, (uint)_bloomMipDown[i].Width, (uint)_bloomMipDown[i].Height);
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

            _bloomUberPost.Dispatch(frameInfo.CommandBuffer,Presenter.FrameIndex, (uint)Screen.Width, (uint)Screen.Height);
        }

    }
}
