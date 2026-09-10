using System.Runtime.CompilerServices;
using VECS.LowLevel;
using Vortice.Vulkan;

namespace VECS
{
    public enum RenderTargetType
    {
        Colour,
        Depth,
        Stencil,
        DepthStencil
    }

    public readonly struct RenderTargetDefintion
    {
        public readonly string Name;
        public readonly int ShaderPropertyId;
        public readonly int TargetDisplay;
        public readonly VkFormat Format;
        public readonly VkExtent2D Extent;
        public readonly VkImageUsageFlags AdditionalUsage;
        public readonly VkImageLayout InputAttachmentLayout;
        public readonly VkImageLayout OutputAttachmentLayout;

        public readonly VkImageLayout InputComputeLayout;
        public readonly VkImageLayout OutputComputeLayout;

        public readonly VkClearValue DefaultClearValue;

        public readonly VkSamplerAddressMode SamplerAddressMode;
        public RenderTargetDefintion(string name)
        {
            Name = name;
            TargetDisplay = -1;
            SamplerAddressMode = VkSamplerAddressMode.ClampToEdge;
        }

        public RenderTargetDefintion(
            string name,
            VkFormat format,
            VkExtent2D extent,
            VkImageUsageFlags usage,
            VkImageLayout inputAttachmentLayout,
            VkImageLayout outputAttachmentLayout,
            VkImageLayout inputComputeLayout,
            VkImageLayout outputComputeLayout
            ) : this(name)
        {
            Format = format;
            Extent = extent;
            AdditionalUsage = usage;
            InputAttachmentLayout = inputAttachmentLayout;
            OutputAttachmentLayout = outputAttachmentLayout;
            InputComputeLayout = inputComputeLayout;
            OutputComputeLayout = outputComputeLayout;
        }

        public RenderTargetDefintion(
            string name,
            VkFormat format,
            VkExtent2D extent,
            VkImageUsageFlags usage,
            VkImageLayout inputAttachmentLayout,
            VkImageLayout outputAttachmentLayout,
            VkImageLayout inputComputeLayout,
            VkImageLayout outputComputeLayout,
            VkClearValue defaultClearValue) : this(name, format, extent, usage, inputAttachmentLayout, outputAttachmentLayout, inputComputeLayout, outputComputeLayout)
        {
            DefaultClearValue = defaultClearValue;
        }

        public RenderTargetDefintion(
            string name,
            int shaderPropertyId,
            VkFormat format,
            VkExtent2D extent,
            VkImageUsageFlags usage,
            VkImageLayout inputAttachmentLayout,
            VkImageLayout outputAttachmentLayout,
            VkImageLayout inputComputeLayout,
            VkImageLayout outputComputeLayout,
            VkClearValue defaultClearValue) : this(name, format, extent, usage, inputAttachmentLayout, outputAttachmentLayout, inputComputeLayout, outputComputeLayout, defaultClearValue)
        {
            ShaderPropertyId = shaderPropertyId;
        }

        public RenderTargetDefintion(
            string name,
            int shaderPropertyId,
            VkFormat format,
            VkExtent2D extent,
            VkImageUsageFlags usage,
            VkImageLayout inputAttachmentLayout,
            VkImageLayout outputAttachmentLayout,
            VkImageLayout inputComputeLayout,
            VkImageLayout outputComputeLayout,
            VkClearValue defaultClearValue,
            VkSamplerAddressMode addressMode) : this(name,shaderPropertyId, format, extent, usage, inputAttachmentLayout, outputAttachmentLayout, inputComputeLayout, outputComputeLayout, defaultClearValue)
        {
            SamplerAddressMode = addressMode;
        }

        public RenderTargetDefintion(
            string name,
            int shaderPropertyId,
            VkFormat format,
            int targetDisplay,
            VkImageUsageFlags usage,
            VkImageLayout inputAttachmentLayout,
            VkImageLayout outputAttachmentLayout,
            VkImageLayout inputComputeLayout,
            VkImageLayout outputComputeLayout,
            VkClearValue defaultClearValue) : this(name, shaderPropertyId, format, new VkExtent2D(), usage, inputAttachmentLayout, outputAttachmentLayout, inputComputeLayout, outputComputeLayout, defaultClearValue)
        {
            TargetDisplay = targetDisplay;
        }


        public RenderTargetDefintion(
            string name,
            VkFormat format,
            int targetDisplay,
            VkImageUsageFlags usage,
            VkImageLayout inputAttachmentLayout,
            VkImageLayout outputAttachmentLayout,
            VkImageLayout inputComputeLayout,
            VkImageLayout outputComputeLayout,
            VkClearValue defaultClearValue) : this(name, format, new VkExtent2D(), usage, inputAttachmentLayout, outputAttachmentLayout, inputComputeLayout, outputComputeLayout, defaultClearValue)
        {
            TargetDisplay = targetDisplay;
        }
    }

    

    public class RenderTarget
    {
        private readonly Texture2D _image;
        private readonly RenderTargetType _renderTargetType;
        public readonly VkImageLayout AttachmentInputLayout;
        public readonly VkImageLayout AttachmentOutputLayout;
        public readonly VkImageLayout ComputeInputLayout;
        public readonly VkImageLayout ComputeOutputLayout;

        public readonly VkClearValue DefaultClearValue;
        public readonly int TargetDisplay;

        public Texture2D Target => _image;
        public VkImageLayout CurrentLayout => _image.ImageLayout;
        public VkImage VkImage => _image._vkImage;
        public VkImageView VkImageView => _image._imageView;

        public RenderTarget(string name, int width, int height, VkFormat format,  VkSamplerAddressMode samplerMode = VkSamplerAddressMode.ClampToEdge)
        {
            VkImageUsageFlags usageFlags;
            var hasDepthFormat = GraphicsDevice.DepthStencilFormats.Contains(format);
            var hasStencilFormat = GraphicsDevice.StencilFormats.Contains(format);
            if (hasDepthFormat || hasStencilFormat)
            {
                usageFlags = VkImageUsageFlags.DepthStencilAttachment | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferSrc | VkImageUsageFlags.TransferDst;
                if(hasDepthFormat && hasStencilFormat)
                {
                    _renderTargetType = RenderTargetType.DepthStencil;
                }
                else if (hasDepthFormat)
                {
                    _renderTargetType = RenderTargetType.Depth;
                }
                else
                {
                    _renderTargetType = RenderTargetType.Stencil;
                }
            }
            else
            {
                usageFlags = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc | VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled;
                _renderTargetType = RenderTargetType.Colour;
            }

            _image = new(string.Format("RT_{0}_{2}_{1}", name, Presenter.FrameCount, _renderTargetType.ToString()), width, height, format, usageFlags, samplerMode, 0, false, VkCompareOp.Never, false);

            if (_renderTargetType == RenderTargetType.Colour)
            {
                _image.SetImageLayout(VkImageLayout.ColorAttachmentOptimal, VkPipelineStageFlags2.None, VkPipelineStageFlags2.ColorAttachmentOutput);
            }
            else
            {
                _image.SetImageLayout(VkImageLayout.DepthStencilAttachmentOptimal, VkPipelineStageFlags2.None, VkPipelineStageFlags2.EarlyFragmentTests);
            }
            CreateAdditionalSamplers();
            DefaultClearValue = new()
            {
                color = new(0, 0, 0, 1),
                depthStencil = new(1, 0)
            };
            TargetDisplay = -1;
        }
        public RenderTarget(string name, int width, int height, VkFormat format, VkImageUsageFlags additionalFlags, VkSamplerAddressMode samplerMode = VkSamplerAddressMode.ClampToEdge)
        {
            VkImageUsageFlags usageFlags = additionalFlags;
            var hasDepthFormat = GraphicsDevice.DepthStencilFormats.Contains(format);
            var hasStencilFormat = GraphicsDevice.StencilFormats.Contains(format);
            if (hasDepthFormat || hasStencilFormat)
            {
                usageFlags |= VkImageUsageFlags.DepthStencilAttachment | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferSrc | VkImageUsageFlags.TransferDst;
                if (hasDepthFormat && hasStencilFormat)
                {
                    _renderTargetType = RenderTargetType.DepthStencil;
                }
                else if (hasDepthFormat)
                {
                    _renderTargetType = RenderTargetType.Depth;
                }
                else
                {
                    _renderTargetType = RenderTargetType.Stencil;
                }
            }
            else
            {
                usageFlags |= VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc | VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled;
                _renderTargetType = RenderTargetType.Colour;
            }

            _image = new(string.Format("RT_{0}_{2}_{1}", name, Presenter.FrameCount, _renderTargetType.ToString()), width, height, format, usageFlags, samplerMode, 0, false, VkCompareOp.Never,VkSamplerMipmapMode.Nearest,VkBorderColor.FloatTransparentBlack,VkFilter.Linear, false);

            if (_renderTargetType == RenderTargetType.Colour)
            {
                _image.SetImageLayout(VkImageLayout.ColorAttachmentOptimal, VkPipelineStageFlags2.None, VkPipelineStageFlags2.ColorAttachmentOutput);
            }
            else
            {
                _image.SetImageLayout(VkImageLayout.DepthStencilAttachmentOptimal, VkPipelineStageFlags2.None, VkPipelineStageFlags2.EarlyFragmentTests);
            }
            CreateAdditionalSamplers();
            DefaultClearValue = new()
            {
                color = new(0, 0, 0, 1),
                depthStencil = new(1, 0)
            };
            TargetDisplay = -1;
        }

        public RenderTarget(string name, int width, int height, VkFormat format, VkClearValue defaultClearValue, VkSamplerAddressMode samplerMode = VkSamplerAddressMode.ClampToEdge) : this(name, width, height, format, samplerMode)
        {
            DefaultClearValue = defaultClearValue;
            TargetDisplay = -1;
        }

        public RenderTarget(string name, int width, int height, VkFormat format, VkClearValue defaultClearValue, VkImageUsageFlags additionalFlags, VkSamplerAddressMode samplerMode = VkSamplerAddressMode.ClampToEdge) : this(name, width, height, format, additionalFlags, samplerMode)
        {
            DefaultClearValue = defaultClearValue;
            TargetDisplay = -1;
        }

        public RenderTarget(RenderTargetDefintion value) : this(value.Name, (int)value.Extent.width,(int)value.Extent.height,value.Format,value.AdditionalUsage)
        {
            AttachmentInputLayout = value.InputAttachmentLayout;
            AttachmentOutputLayout = value.OutputAttachmentLayout;
            ComputeInputLayout = value.InputComputeLayout;
            ComputeOutputLayout = value.OutputComputeLayout;
            DefaultClearValue = value.DefaultClearValue;
            TargetDisplay = value.TargetDisplay;
        }
        public RenderTarget(RenderTargetDefintion value, VkExtent2D extent) : this(value.Name, (int)extent.width, (int)extent.height, value.Format, value.AdditionalUsage)
        {
            AttachmentInputLayout = value.InputAttachmentLayout;
            AttachmentOutputLayout = value.OutputAttachmentLayout;
            ComputeInputLayout = value.InputComputeLayout;
            ComputeOutputLayout = value.OutputComputeLayout;
            DefaultClearValue = value.DefaultClearValue;
            TargetDisplay = value.TargetDisplay;


        }
        public VkImageBlit GetBlitCmd(int dstWidth, int dstHeight, VkImageAspectFlags dstAspectMask)
        {
            VkImageBlit imageBlit = new()
            {
                srcSubresource = new()
                {
                    aspectMask = dstAspectMask,
                    layerCount = 1,
                    mipLevel = 0,

                },
                dstSubresource = new()
                {
                    aspectMask = _renderTargetType switch
                    {
                        RenderTargetType.Colour => VkImageAspectFlags.Color,
                        RenderTargetType.Depth => VkImageAspectFlags.Depth,
                        RenderTargetType.Stencil => VkImageAspectFlags.Stencil,
                        RenderTargetType.DepthStencil => VkImageAspectFlags.Depth | VkImageAspectFlags.Stencil,
                        _=> VkImageAspectFlags.None
                    },
                    layerCount = 1,
                    mipLevel = 0
                }
            };
            imageBlit.srcOffsets[1].x = Target.Width;
            imageBlit.srcOffsets[1].y = Target.Height;
            imageBlit.srcOffsets[1].z = 1;

            imageBlit.dstOffsets[1].x = dstWidth;
            imageBlit.dstOffsets[1].y = dstHeight;
            imageBlit.dstOffsets[1].z = 1;

            return imageBlit;
        }

        private unsafe void CreateAdditionalSamplers()
        {

            if (_renderTargetType != RenderTargetType.Colour)
            {
                VkSamplerCreateInfo createInfo = new()
                {
                    magFilter = VkFilter.Linear,
                    minFilter = VkFilter.Linear,
                    mipmapMode = VkSamplerMipmapMode.Nearest,
                    addressModeU = VkSamplerAddressMode.ClampToEdge,
                    addressModeV = VkSamplerAddressMode.ClampToEdge,
                    addressModeW = VkSamplerAddressMode.ClampToEdge,
                    minLod = 0,
                    maxLod = 16.0f
                };


                if (VkSamplerReductionMode.Min != VkSamplerReductionMode.WeightedAverage)
                {
                    VkSamplerReductionModeCreateInfo createInfoReduction = new()
                    {
                        reductionMode = VkSamplerReductionMode.Min
                    };

                    createInfo.pNext = &createInfoReduction;
                }
                _image.CreateSampler(createInfo);
            }

        }

        public void Resize(int width, int height)
        {
            if (_image.Width == width && _image.Height == height) return;
            _image.Reinitialise(width, height);

            if (_renderTargetType == RenderTargetType.Colour)
            {
                _image.SetImageLayout(VkImageLayout.ColorAttachmentOptimal, VkPipelineStageFlags2.None, VkPipelineStageFlags2.ColorAttachmentOutput);
            }
            else
            {
                _image.SetImageLayout(VkImageLayout.DepthStencilAttachmentOptimal, VkPipelineStageFlags2.None, VkPipelineStageFlags2.EarlyFragmentTests);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public VkRect2D GetFullRenderArea()
        {
            return new(0, 0, (uint)Target.Width, (uint)Target.Height);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public VkRenderingAttachmentInfo GetAttachmentInfo(VkAttachmentLoadOp loadOp = VkAttachmentLoadOp.Clear, VkAttachmentStoreOp storeOp = VkAttachmentStoreOp.Store)
        {
            return GetAttachmentInfo(DefaultClearValue,loadOp, storeOp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public VkRenderingAttachmentInfo GetAttachmentInfo(VkClearValue clear, VkAttachmentLoadOp loadOp = VkAttachmentLoadOp.Clear, VkAttachmentStoreOp storeOp = VkAttachmentStoreOp.Store)
        {
            return new()
            {
                clearValue = clear,
                imageLayout = CurrentLayout,
                imageView = VkImageView,
                loadOp = loadOp,
                storeOp = storeOp,
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginRenderingOnlyAttachment(VkCommandBuffer commandBuffer, VkAttachmentLoadOp loadOp = VkAttachmentLoadOp.Clear, VkAttachmentStoreOp storeOp = VkAttachmentStoreOp.Store)
        {
            BeginRenderingOnlyAttachment(commandBuffer,DefaultClearValue,loadOp,storeOp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void BeginRenderingOneAttachmentWithDepthStencil(VkCommandBuffer commandBuffer, VkRenderingAttachmentInfo depthStencil, VkAttachmentLoadOp colourLoadOp = VkAttachmentLoadOp.Clear, VkAttachmentStoreOp colourStoreOp = VkAttachmentStoreOp.Store)
        {
            var colourAttachment = GetAttachmentInfo(colourLoadOp, colourStoreOp);
            BeginRenderingMultiAttachment(commandBuffer, 1, &colourAttachment, 1, depthStencil);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void BeginRenderingOnlyAttachment(VkCommandBuffer commandBuffer, VkClearValue clearValue, VkAttachmentLoadOp loadOp = VkAttachmentLoadOp.Clear, VkAttachmentStoreOp storeOp = VkAttachmentStoreOp.Store)
        {
            
            switch (_renderTargetType)
            {
                case RenderTargetType.Colour when(CurrentLayout != VkImageLayout.ColorAttachmentOptimal):
                    Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.ColorAttachmentOptimal);
                    break;
                case RenderTargetType.Depth when(CurrentLayout != VkImageLayout.DepthAttachmentOptimal):
                    Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.DepthAttachmentOptimal);
                    break;
                case RenderTargetType.Stencil when(CurrentLayout != VkImageLayout.StencilAttachmentOptimal):
                    Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.StencilAttachmentOptimal);
                    break;
                case RenderTargetType.DepthStencil when(CurrentLayout != VkImageLayout.DepthStencilAttachmentOptimal):
                    Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.DepthStencilAttachmentOptimal);
                    break;
            }

            var attachmentInfo = GetAttachmentInfo(clearValue, loadOp, storeOp);
            VkRenderingInfo renderingInfo = new()
            {
                renderArea = GetFullRenderArea(),
                layerCount = 1,
                flags = VkRenderingFlags.ContentsInlineKHR | VkRenderingFlags.ContentsSecondaryCommandBuffers
            };
            switch (_renderTargetType)
            {
                case RenderTargetType.Colour:
                    renderingInfo.colorAttachmentCount = 1;
                    renderingInfo.pColorAttachments = &attachmentInfo;
                    
                    break;
                case RenderTargetType.Depth:
                    renderingInfo.pDepthAttachment = &attachmentInfo;
                    break;
                case RenderTargetType.Stencil:
                    renderingInfo.pStencilAttachment = &attachmentInfo;
                    break;
                case RenderTargetType.DepthStencil:
                    renderingInfo.pDepthAttachment = &attachmentInfo;
                    renderingInfo.pStencilAttachment = &attachmentInfo;
                    break;
            }
            GraphicsDevice.DeviceAPI.vkCmdBeginRendering(commandBuffer, &renderingInfo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearAttachment(VkCommandBuffer commandBuffer)
        {
            var layoutAfterRender = CurrentLayout;
            ClearAttachment(commandBuffer, DefaultClearValue);
            if(CurrentLayout != layoutAfterRender)
            {
                Target.SetImageLayoutAuto(commandBuffer, layoutAfterRender);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearAttachment(VkCommandBuffer commandBuffer, VkClearValue clearValue)
        {
            BeginRenderingOnlyAttachment(commandBuffer, clearValue);
            GraphicsDevice.DeviceAPI.vkCmdEndRendering(commandBuffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void BeginRenderingMultiAttachment(VkCommandBuffer commandBuffer, int layCount, VkRenderingAttachmentInfo* colourAttachments, int attachmentCount)
        {
            BeginRenderingMultiAttachment(commandBuffer, layCount, colourAttachments, attachmentCount, null, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void BeginRenderingMultiAttachment(VkCommandBuffer commandBuffer, int layCount, VkRenderingAttachmentInfo* colourAttachments, int attachmentCount, VkRenderingAttachmentInfo depth)
        {
            BeginRenderingMultiAttachment(commandBuffer, layCount, colourAttachments, attachmentCount, &depth, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void BeginRenderingMultiAttachment(VkCommandBuffer commandBuffer, int layCount, VkRenderingAttachmentInfo* colourAttachments, int attachmentCount, VkRenderingAttachmentInfo depth, VkRenderingAttachmentInfo stencil)
        {
            BeginRenderingMultiAttachment(commandBuffer, layCount, colourAttachments, attachmentCount, &depth, &stencil);
        }

        private unsafe void BeginRenderingMultiAttachment(VkCommandBuffer commandBuffer, int layCount,VkRenderingAttachmentInfo* colourAttachments, int attachmentCount, VkRenderingAttachmentInfo* depth, VkRenderingAttachmentInfo* stencil )
        {
            VkRenderingInfo renderingInfo = new()
            {
                renderArea = GetFullRenderArea(),
                layerCount = (uint)layCount,
                colorAttachmentCount = (uint)attachmentCount,
                pColorAttachments = colourAttachments,
                pDepthAttachment = depth,
                pStencilAttachment = stencil,
                flags = VkRenderingFlags.ContentsInlineKHR | VkRenderingFlags.ContentsSecondaryCommandBuffers
            };
            GraphicsDevice.DeviceAPI.vkCmdBeginRendering(commandBuffer, &renderingInfo);
        }
    }
}
