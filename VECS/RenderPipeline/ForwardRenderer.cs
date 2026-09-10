using System;
using VECS.ECS;
using VECS.LowLevel;
using Vortice.Vulkan;

namespace VECS
{
    public class ForwardRenderer : IRenderer
    {
        const int DEPTH_ONLY_PUSH_CONSTANT_INDEX = 0;
        public RenderTarget MainColourAttachment { get; private set; }
        public RenderTarget BrightObjectAttachment;
        public RenderTarget DepthAttachment;

        private DepthOnlyQueue _depthOnlyQueue;
        private ForwardQueue _forwardQueue;

        private OIT _orderIndpTransparency;
        private SMAA _smaa;

        public VkFormat MainColourFormat => VkFormat.R16G16B16A16Sfloat;
        public VkFormat PostProcessingColourFormat => VkFormat.B10G11R11UfloatPack32;

        public VkFormat DepthFormat => PreferredFormats.LOW_PRECISION_DEPTH_ONLY;
        public VkFormat StencilFormat => VkFormat.Undefined;
        private Action _onScreenSizeChanged;
        public Action OnScreenSizeChanged{get=> _onScreenSizeChanged;set => _onScreenSizeChanged = value;}

        public ForwardRenderer()
        {

        }

        public void PostCreate()
        {
            ScreenSizeChanged();

            _depthOnlyQueue = new DepthOnlyQueue("DepthOnly");
            _forwardQueue = new ForwardQueue("Forward");

            EnginePipes.DepthOnly.PushConstants.SetPushConstantInt("layerCount", DEPTH_ONLY_PUSH_CONSTANT_INDEX, 1);
            EnginePipes.DepthOnly.PushConstants.SetPushConstantInt("bufferSelect", DEPTH_ONLY_PUSH_CONSTANT_INDEX, 0);
            _orderIndpTransparency = new(this);
            _smaa = new(this);
            Skybox.StartSkybox();
            PBR.StartPBR();
        }

        public void ScreenSizeChanged()
        {
            EngineBuffers.RemoveEngineBuffer(ShaderProperties.LinkedListSBOId);
            var windowExtents = Application.MainWindow.WindowExtent;

            MainColourAttachment = IRenderer.CreateOrUpdateRT(MainColourAttachment, "MainColourAttachment", ShaderProperties.MainColourAttachmentId, windowExtents, MainColourFormat, new VkClearValue(0, 0, 0, 1));
            DepthAttachment = IRenderer.CreateOrUpdateRT(DepthAttachment, "DepthAttacment", ShaderProperties.MainDepthAttachmentId, windowExtents, DepthFormat, new VkClearValue(1,0));

            _smaa?.RecreateRenderTargets();
            _onScreenSizeChanged?.Invoke();
        }

        public void PreRender()
        {

        }

        public unsafe void Render(RendererFrameInfo frameInfo, int imageIndex)
        {

            if (Presenter.FrameCount == 2)
            {
                PBR.Generate_BRDFLUT(frameInfo);
                PBR.Generate_Irradiance(frameInfo);
                PBR.Generate_Prefiltered_Cubemap(frameInfo);
            }

            // blit renderImage into swapchain
            var extents = SwapChain.SwapChainExtent;
            GraphicsDevice.BeginLabelCmd(frameInfo.CommandBuffer, "SwapChain Blit");
            BlitFromMainColour(frameInfo.CommandBuffer, SwapChain.MainSwapChainData.SwapChainImages[imageIndex], (int)extents.width, (int)extents.height, VkImageAspectFlags.Color);
            GraphicsDevice.EndLabelCmd(frameInfo.CommandBuffer);
        }

        public void PostRender()
        {

        }

        public void StartForwardRendering(RendererFrameInfo frameInfo, VkAttachmentLoadOp colourLoad)
        {
            StartForwardRendering(frameInfo.CommandBuffer, colourLoad);
        }

        public unsafe void StartForwardRendering(VkCommandBuffer commandBuffer, VkAttachmentLoadOp colourLoad, bool onlyMainAttachment = false, bool noDepth = false)
        {
            if (MainColourAttachment.CurrentLayout == VkImageLayout.TransferSrcOptimal)
            {
                MainColourAttachment.Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.ColorAttachmentOptimal);
            }
            if (MainColourAttachment.CurrentLayout == VkImageLayout.ShaderReadOnlyOptimal)
            {
                MainColourAttachment.Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.ColorAttachmentOptimal);
            }
            BrightObjectAttachment.Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.ColorAttachmentOptimal);

            VkRenderingAttachmentInfo* colourAttachments = stackalloc VkRenderingAttachmentInfo[]
            {
                MainColourAttachment.GetAttachmentInfo(colourLoad),

                BrightObjectAttachment.GetAttachmentInfo(colourLoad)
            };

            MainColourAttachment.BeginRenderingMultiAttachment(commandBuffer, 1, colourAttachments, onlyMainAttachment ? 1 : 2, DepthAttachment.GetAttachmentInfo());

            Presenter.SetToCurrentCameraViewportScissor(commandBuffer);
        }

        public void EndForwardRendering(RendererFrameInfo frameInfo)
        {
            GraphicsDevice.DeviceAPI.vkCmdEndRendering(frameInfo.CommandBuffer);
        }

        public void ClearForwardDepthAttachment(VkCommandBuffer commandBuffer)
        {
            DepthAttachment.Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.DepthAttachmentOptimal);

            DepthAttachment.ClearAttachment(commandBuffer, new(1, 0));

            DepthAttachment.Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.DepthAttachmentOptimal);
        }

        public unsafe void BeginDepthOnlyRendering(VkCommandBuffer commandBuffer, VkAttachmentLoadOp loadOp)
        {
            DepthAttachment.BeginRenderingOnlyAttachment(commandBuffer, loadOp);
            Presenter.SetToCurrentCameraViewportScissor(commandBuffer);
        }

        public void EndDepthOnlyRendering(VkCommandBuffer commandBuffer)
        {
            GraphicsDevice.DeviceAPI.vkCmdEndRendering(commandBuffer);

            // PLEASE TRY REMOVING THIS BARRIER ON NV TO SEE IF IT CASUES FLICKERING
            uint graphicsFamily = GraphicsDevice.PhysicalQueueFamilies.graphicsFamily;

            MemoryBarrierHelper.ImageMemoryBarrier(commandBuffer,
                DepthAttachment.VkImage,
                DepthAttachment.Target.GetSubresourceRange(),
                VkPipelineStageFlags2.EarlyFragmentTests | VkPipelineStageFlags2.LateFragmentTests,
                VkAccessFlags2.DepthStencilAttachmentRead | VkAccessFlags2.DepthStencilAttachmentWrite,
                VkPipelineStageFlags2.EarlyFragmentTests | VkPipelineStageFlags2.LateFragmentTests,
                VkAccessFlags2.DepthStencilAttachmentRead | VkAccessFlags2.DepthStencilAttachmentWrite,
                VkImageLayout.DepthStencilAttachmentOptimal,
                VkImageLayout.DepthStencilAttachmentOptimal,
                graphicsFamily, graphicsFamily
            );
        }

        public void BlitFromMainColour(VkCommandBuffer commandBuffer, VkImage dst, int dstWidth, int dstHeight, VkImageAspectFlags dstAspectMask)
        {
            MainColourAttachment.Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.TransferSrcOptimal);

            TextureExtensions.BlitGeneric(commandBuffer, VkFilter.Linear, MainColourAttachment.GetBlitCmd(dstWidth, dstHeight, dstAspectMask), MainColourAttachment.VkImage, MainColourAttachment.CurrentLayout, dst, VkImageLayout.TransferDstOptimal);

            MainColourAttachment.Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.ColorAttachmentOptimal);

        }
    }
}
