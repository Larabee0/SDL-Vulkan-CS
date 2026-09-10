using Vortice.Vulkan;

namespace VECS
{
    public interface IRenderer
    {
        public VkFormat MainColourFormat { get; }
        public VkFormat PostProcessingColourFormat { get; }
        public VkFormat DepthFormat { get; }
        public VkFormat StencilFormat { get; }

        public RenderTarget MainColourAttachment { get; }
        
        public void PostCreate();
        public void ScreenSizeChanged();
        public void PreRender();
        public void Render(RendererFrameInfo frameInfo, int imageIndex);
        public void PostRender();

        public void StartForwardRendering(RendererFrameInfo frameInfo, VkAttachmentLoadOp loadOp);
        public void EndForwardRendering(RendererFrameInfo frameInfo);

        public static RenderTarget CreateOrUpdateRT(RenderTarget target, string name, int shaderPropertyId, VkExtent2D extent, VkFormat format)
        {
            if (target == null)
            {
                target = new(name, (int)extent.width, (int)extent.height, format);
                EngineTextures.AddOrUpdateTexture(shaderPropertyId, (SingleTexture)target.Target);
            }
            else
            {
                target.Resize((int)extent.width, (int)extent.height);
            }
            return target;
        }
        public static RenderTarget CreateOrUpdateRT(RenderTarget target, string name, int shaderPropertyId, VkExtent2D extent, VkFormat format, VkClearValue defaultClearValue)
        {
            if (target == null)
            {
                target = new(name, (int)extent.width, (int)extent.height, format, defaultClearValue);
                
                EngineTextures.AddOrUpdateTexture(shaderPropertyId, (SingleTexture)target.Target);
            }
            else
            {
                target.Resize((int)extent.width, (int)extent.height);
            }
            return target;
        }

        public static RenderTarget CreateOrUpdateRT(RenderTarget target, string name, int shaderPropertyId, VkExtent2D extent, VkFormat format, VkClearValue defaultClearValue, VkSamplerAddressMode samplerMode)
        {
            if (target == null)
            {
                target = new(name, (int)extent.width, (int)extent.height, format, defaultClearValue, samplerMode);

                EngineTextures.AddOrUpdateTexture(shaderPropertyId, (SingleTexture)target.Target);
            }
            else
            {
                target.Resize((int)extent.width, (int)extent.height);
            }
            return target;
        }

        public static RenderTarget CreateOrUpdateRT(RenderTarget target, string name, int shaderPropertyId, VkExtent2D extent, VkFormat format, VkImageUsageFlags additionalFlags)
        {
            if (target == null)
            {
                target = new(name, (int)extent.width, (int)extent.height, format, additionalFlags);
                EngineTextures.AddOrUpdateTexture(shaderPropertyId, (SingleTexture)target.Target);
            }
            else
            {
                target.Resize((int)extent.width, (int)extent.height);
            }
            return target;
        }
        public static RenderTarget CreateOrUpdateRT(RenderTarget target, string name, int shaderPropertyId, VkExtent2D extent, VkFormat format, VkImageUsageFlags additionalFlags, VkClearValue defaultClearValue)
        {
            if (target == null)
            {
                target = new(name, (int)extent.width, (int)extent.height, format, defaultClearValue, additionalFlags);
                EngineTextures.AddOrUpdateTexture(shaderPropertyId, (SingleTexture)target.Target);
            }
            else
            {
                target.Resize((int)extent.width, (int)extent.height);
            }
            return target;
        }

        public static RenderTarget CreateOrUpdateRT(RenderTarget target, RenderTargetDefintion defintion, VkExtent2D extent)
        {
            if(target == null)
            {
                target = new(defintion, extent);
                EngineTextures.AddOrUpdateTexture(defintion.ShaderPropertyId, (SingleTexture)target.Target);
            }
            else
            {
                target.Resize((int)extent.width, (int)extent.height);
            }
            return target;
        }

        public static void UpdateRT(RenderTarget target, VkExtent2D newExtent)
        {
            target.Resize((int)newExtent.width, (int)newExtent.height);
        }
    }
}
