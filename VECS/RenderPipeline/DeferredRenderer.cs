using System;
using System.Runtime.CompilerServices;
using VECS.ECS;
using VECS.LowLevel;
using Vortice.Vulkan;

namespace VECS
{
    public class DeferredRenderer : IRenderer
    {
        const int DEPTH_ONLY_PUSH_CONSTANT_INDEX = 0;

        public RenderTarget MainColourAttachment { get; private set; }
        public RenderTarget BrightObjectAttachment;
        public RenderTarget DepthAttachment;

        public RenderTarget PostProcessingAttachment;

        public static readonly int G_PositionPropertyId = "g_PositionIn".GetShaderPropertyId();
        public static readonly int G_NormalsPropertyId = "g_NormalsIn".GetShaderPropertyId();
        public static readonly int G_AlbedoPropertyId = "g_AlbedoIn".GetShaderPropertyId();
        public static readonly int G_MaskPropertyId = "g_MaskIn".GetShaderPropertyId();
        public static readonly int IntermediateColourPropertyId = "colourIn".GetShaderPropertyId();

        public RenderTarget G_PositionAttachment;
        public RenderTarget G_NormalAttachment;
        public RenderTarget G_AlbedoAttachment;
        public RenderTarget G_MaskAttachment;

        public static bool useRenderGraph = true;

        private OIT _orderIndpTransparency;
        private Bloom _bloom;
        private SMAA _smaa;
        private SSAO _ssao;
        private UnityPhyBloom _phyBloom;

        private DepthOnlyQueue _depthOnlyQueue;
        private DeferredQueue _deferredQueue;
        private ForwardQueue _forwardQueue;

        private static ComputeVariant _deferredComposite;

        public static readonly VkFormat[] Colours = [VkFormat.R32G32B32A32Sfloat, VkFormat.R32G32B32A32Sfloat];
        public VkFormat[] ColourFormats => Colours;

        public VkFormat DepthFormat => PreferredFormats.LOW_PRECISION_DEPTH_ONLY;

        public VkFormat StencilFormat => VkFormat.Undefined;
        private Action _onScreenSizeChanged;
        public Action OnScreenSizeChanged{get=> _onScreenSizeChanged;set => _onScreenSizeChanged = value;}

        public DeferredRenderer()
        {
            _deferredComposite = ComputePipeline.GetOrCreate("pbr_composit.comp").Default();

            RenderGraph.AddResource(new("MainColourAttachment", ShaderProperties.MainColourAttachmentId, ColourFormats[0], 0,
                VkImageUsageFlags.Storage,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.ColorAttachmentOptimal,
                VkImageLayout.General,
                VkImageLayout.General,
                new(0, 0, 0, 1)));

            RenderGraph.AddResource(new("PostProcessingColourAttachment", "PostProcessingAttachment".GetShaderPropertyId(), VkFormat.B10G11R11UfloatPack32, 0,
                VkImageUsageFlags.Storage,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.ColorAttachmentOptimal,
                VkImageLayout.General,
                VkImageLayout.General,
                new(0, 0, 0, 1)));

            RenderGraph.AddResource(new("BrightObjectAttachment", ShaderProperties.BrightColourAttachmentId, ColourFormats[1], 0,
                VkImageUsageFlags.Storage,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.ColorAttachmentOptimal,
                VkImageLayout.General,
                VkImageLayout.General,
                new(0, 0, 0, 1)));

            RenderGraph.AddResource(new("MainDepthAttachment", ShaderProperties.MainDepthAttachmentId, DepthFormat, 0,
                VkImageUsageFlags.None,
                VkImageLayout.DepthAttachmentOptimal,
                VkImageLayout.DepthAttachmentOptimal,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.General,
                new(1, 0)));

            

            RenderGraph.AddResource(new("G_PositionAttachment", G_PositionPropertyId, VkFormat.R16G16B16A16Sfloat, 0,
                VkImageUsageFlags.Storage,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.ColorAttachmentOptimal,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.General,
                new(0, 0, 0, 0)));
            RenderGraph.AddResource(new("G_NormalAttachment", G_NormalsPropertyId, VkFormat.R16G16B16A16Sfloat, 0,
                VkImageUsageFlags.Storage,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.ColorAttachmentOptimal,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.General,
                new(0, 0, 0, 0)));
            RenderGraph.AddResource(new("G_AlbedoAttachment", G_AlbedoPropertyId, VkFormat.R8G8B8A8Unorm, 0,
                VkImageUsageFlags.Storage,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.ColorAttachmentOptimal,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.General,
                new(0, 0, 0, 0)));
            RenderGraph.AddResource(new("G_MaskAttachment", G_MaskPropertyId, VkFormat.R8G8B8A8Unorm, 0,
                VkImageUsageFlags.Storage,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.ColorAttachmentOptimal,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.General,
                new(0, 0, 0, 0)));

            RenderGraph.AddPass("DeferredDepthOnlyPass", PassType.Render,
                [], [],
                ["MainDepthAttachment"], DeferredDepthPass);

            RenderGraph.AddPass("DeferredObjectsPass", PassType.Render,
                ["DeferredDepthOnlyPass"],
                ["MainDepthAttachment"],
                ["G_PositionAttachment", "G_NormalAttachment", "G_AlbedoAttachment", "G_MaskAttachment"], DeferredObjectsPass);

            RenderGraph.AddPass("DeferredCompositePass", PassType.Compute,
                [
                    "SSAO_Blur",
                    "DeferredObjectsPass",
                    "SpotLightShadows",
                    "PointLightShadows",
                    "DirectionalLightShadows"
                ],
                ["SSAO_BLUR_RT",
                "G_PositionAttachment",
                "G_NormalAttachment",
                "G_AlbedoAttachment",
                "G_MaskAttachment",
                "DirectionalShadowAttachment",
                "PointLightShadowAttachments",
                "SpotLightShadowAttachments"],
                ["MainColourAttachment"], DeferredCompositePass);

            RenderGraph.AddPass("ForwardPass", PassType.Render,
                ["ForwardDepthOnlyPass",
                    "SpotLightShadows",
                    "PointLightShadows",
                    "DirectionalLightShadows",
                    "DeferredCompositePass"],
                ["MainDepthAttachment", "DirectionalShadowAttachment", "PointLightShadowAttachments", "SpotLightShadowAttachments"],
                ["MainColourAttachment", "BrightObjectAttachment",], ForwardPass);

        }

        public static void SetExposure(float exposure)
        {
            _deferredComposite?.PushConstantsHandler?.SetPushConstantFloat("exposure", 0,exposure);
        }

        public static void SetGamma(float gamma)
        {
            _deferredComposite?.PushConstantsHandler?.SetPushConstantFloat("gamma", 0, gamma);
        }

        public void PostCreate()
        {
            EnginePipes.DepthOnly.PushConstants.SetPushConstantInt("layerCount", DEPTH_ONLY_PUSH_CONSTANT_INDEX, 1);
            EnginePipes.DepthOnly.PushConstants.SetPushConstantInt("bufferSelect", DEPTH_ONLY_PUSH_CONSTANT_INDEX, 0);
            _orderIndpTransparency = new(this);
            _bloom = new(this);
            _smaa = new(this);
            _ssao = new(this);
            _phyBloom = new(this);
            Skybox.StartSkybox();
            PBR.StartPBR();

            ScreenSizeChanged();
            _depthOnlyQueue = new DepthOnlyQueue("DepthOnly");
            _deferredQueue = new DeferredQueue("Deferred");
            _forwardQueue = new ForwardQueue("Forward");
            DrawBlob.AddQueue(_depthOnlyQueue);
            DrawBlob.AddQueue(_deferredQueue);
            DrawBlob.AddQueue(_forwardQueue);

        }

        public void ScreenSizeChanged()
        {
            var windowExtents = Application.MainWindow.WindowExtent;

            RenderGraph.RecreateAttachments(0,windowExtents);

            MainColourAttachment = RenderGraph.GetResource("MainColourAttachment");
            BrightObjectAttachment = RenderGraph.GetResource("BrightObjectAttachment");
            DepthAttachment = RenderGraph.GetResource("MainDepthAttachment");
            PostProcessingAttachment = RenderGraph.GetResource("PostProcessingColourAttachment");
            G_PositionAttachment = RenderGraph.GetResource("G_PositionAttachment");
            G_NormalAttachment = RenderGraph.GetResource("G_NormalAttachment");
            G_AlbedoAttachment = RenderGraph.GetResource("G_AlbedoAttachment");
            G_MaskAttachment = RenderGraph.GetResource("G_MaskAttachment");
            
            _orderIndpTransparency?.RecreateRenderTargets();
            _bloom?.RecreateRenderTargets();
            _smaa?.RecreateRenderTargets();
            _ssao?.RecreateRenderTargets();
            _phyBloom?.RecreateRenderTargets();
            SetDeferredResources();
            _onScreenSizeChanged?.Invoke();
        }

        private void SetDeferredResources()
        {
            var windowExtents = Application.MainWindow.WindowExtent;
            _deferredComposite.SetStorageBuffer(ShaderProperties.DirectionalLightsBufferId, EngineBuffers.TryGetBuffer(ShaderProperties.DirectionalLightsBufferId));
            _deferredComposite.SetStorageBuffer(ShaderProperties.PointLightsBufferId, EngineBuffers.TryGetBuffer(ShaderProperties.PointLightsBufferId));
            _deferredComposite.SetStorageBuffer(ShaderProperties.SpotLightsBufferId, EngineBuffers.TryGetBuffer(ShaderProperties.SpotLightsBufferId));
            _deferredComposite.SetStorageBuffer(ShaderProperties.CameraDataId, EngineBuffers.TryGetBuffer(ShaderProperties.CameraDataId));

            _deferredComposite.SetTextures(ShaderProperties.DirShadowImageId, EngineTextures.TryGetTexture(ShaderProperties.DirShadowImageId));
            _deferredComposite.SetTextures(ShaderProperties.PLShadowImageId, EngineTextures.TryGetTexture(ShaderProperties.PLShadowImageId));
            _deferredComposite.SetTextures(ShaderProperties.SLShadowImageId, EngineTextures.TryGetTexture(ShaderProperties.SLShadowImageId));

            _deferredComposite.SetTextures(G_PositionPropertyId, EngineTextures.TryGetTexture(G_PositionPropertyId));
            _deferredComposite.SetTextures(G_NormalsPropertyId, EngineTextures.TryGetTexture(G_NormalsPropertyId));
            _deferredComposite.SetTextures(G_AlbedoPropertyId, EngineTextures.TryGetTexture(G_AlbedoPropertyId));
            _deferredComposite.SetTextures(G_MaskPropertyId, EngineTextures.TryGetTexture(G_MaskPropertyId));
            _deferredComposite.SetTextures(SSAO.SSAO_Blur_RT_PropertyId, EngineTextures.TryGetTexture(SSAO.SSAO_Blur_RT_PropertyId));

            _deferredComposite.SetTexture("outImage".GetShaderPropertyId(), MainColourAttachment.Target);
            _deferredComposite.PushConstantsHandler.SetPushConstantVector2("outputImageSize", 0, new(windowExtents.width, windowExtents.height));
        }

        public void PreRender()
        {
            SSAO.SSAO_Toggle_Input();
            UnityPhyBloom.Bloom_Toggle_Input();
        }

        public unsafe void Render(RendererFrameInfo frameInfo, int imageIndex)
        {
            if (Presenter.FrameCount == 0)
            {
                GraphicsDevice.BeginLabelCmd(frameInfo.CommandBuffer, "PBR Maps");
                PBR.Generate_Prefiltered_Cubemap(frameInfo);
                PBR.Generate_BRDFLUT(frameInfo);
                PBR.Generate_Irradiance(frameInfo);
                _deferredComposite?.SetTexture("samplerIrradiance".GetShaderPropertyId(), EngineTextures.TryGetTexture("samplerIrradiance".GetShaderPropertyId()).First);
                _deferredComposite?.SetTexture("prefilteredMap".GetShaderPropertyId(), EngineTextures.TryGetTexture("prefilteredMap".GetShaderPropertyId()).First);
                _deferredComposite?.SetTexture("samplerBRDFLUT".GetShaderPropertyId(), EngineTextures.TryGetTexture("samplerBRDFLUT".GetShaderPropertyId()).First);
                GraphicsDevice.EndLabelCmd(frameInfo.CommandBuffer);
            }

            GraphicsDevice.BeginLabelCmd(frameInfo.CommandBuffer, "Render Graph");
            RenderGraph.Execute(frameInfo);
            GraphicsDevice.EndLabelCmd(frameInfo.CommandBuffer);

            // blit renderImage into swapchain
            var extents = SwapChain.SwapChainExtent;
            GraphicsDevice.BeginLabelCmd(frameInfo.CommandBuffer, "SwapChain Blit");
            BlitFromPostProcessingColour(frameInfo.CommandBuffer, SwapChain.MainSwapChainData.SwapChainImages[imageIndex], (int)extents.width, (int)extents.height, VkImageAspectFlags.Color);

            GraphicsDevice.EndLabelCmd(frameInfo.CommandBuffer);
        }

        private void ForwardPass(RendererFrameInfo frameInfo)
        {
            if (_forwardQueue.CommandCount > 0)
            {
                DrawBlob.Cull(_forwardQueue, frameInfo, frameInfo.CullData);
            }
            StartForwardRendering(frameInfo.CommandBuffer, VkAttachmentLoadOp.Load, false, false);
            if (_forwardQueue.CommandCount > 0)
            {
                DrawBlob.Execute(_forwardQueue, frameInfo, 0, VkCullModeFlags.Back);
            }
            // skybox last item rendered to save fragments from any depth writes
            GraphicsDevice.BeginLabelCmd(frameInfo.CommandBuffer, "Skybox");
            Skybox.RenderSkybox(frameInfo);
            GraphicsDevice.EndLabelCmd(frameInfo.CommandBuffer);

            EndForwardRendering(frameInfo);
        }

        private void DeferredDepthPass(RendererFrameInfo frameInfo)
        {
            var commandBuffer = frameInfo.CommandBuffer;
            if (_depthOnlyQueue.CommandCount > 0)
            {
                EnginePipes.DepthOnly.PushConstants.SetPushConstantInt("matrixStartIndex", DEPTH_ONLY_PUSH_CONSTANT_INDEX, frameInfo.MainCamera);

                var depthBufferCullInfo = frameInfo.CullData;
                depthBufferCullInfo.cullMode &= ~CullModeFlags.Depth;


                DrawBlob.Cull(_depthOnlyQueue, frameInfo, depthBufferCullInfo);

                BeginDepthOnlyRendering(commandBuffer, VkAttachmentLoadOp.Clear);

                DrawBlob.Execute(_depthOnlyQueue, frameInfo, DEPTH_ONLY_PUSH_CONSTANT_INDEX, VkCullModeFlags.Back);

                GraphicsDevice.DeviceAPI.vkCmdEndRendering(commandBuffer);

                DepthReduction.ReduceDepth(frameInfo);
            }
            else
            {
                GraphicsDevice.BeginLabelCmd(frameInfo.CommandBuffer, "Clear Main Depth Only");
                DepthAttachment.ClearAttachment(commandBuffer);
                GraphicsDevice.EndLabelCmd(frameInfo.CommandBuffer);

                DepthReduction.ClearPyramid(frameInfo);
            }
        }

        private void ForwadDepthPass(RendererFrameInfo frameInfo)
        {
            if (_forwardQueue.CommandCount > 0)
            {
                var commandBuffer = frameInfo.CommandBuffer;
                EnginePipes.DepthOnly.PushConstants.SetPushConstantInt("matrixStartIndex", DEPTH_ONLY_PUSH_CONSTANT_INDEX, frameInfo.MainCamera);
                var depthBufferCullInfo = frameInfo.CullData;
                depthBufferCullInfo.cullMode &= ~CullModeFlags.Depth;
                GraphicsDevice.BeginLabelCmd(frameInfo.CommandBuffer, "Forward Depth Only");
                DrawBlob.Cull(_forwardQueue, frameInfo, depthBufferCullInfo);
                BeginDepthOnlyRendering(commandBuffer, VkAttachmentLoadOp.Load);
                //DrawBlob.Execute(_forwardQueue, frameInfo, DEPTH_ONLY_PUSH_CONSTANT_INDEX, VkCullModeFlags.Back);
                GraphicsDevice.DeviceAPI.vkCmdEndRendering(commandBuffer);
                GraphicsDevice.EndLabelCmd(frameInfo.CommandBuffer);

                DepthReduction.ReduceDepth(frameInfo);
            }
        }

        private void DeferredObjectsPass(RendererFrameInfo frameInfo)
        {
            DrawBlob.Cull(_deferredQueue, frameInfo, frameInfo.CullData);

            StartDeferredRendering(frameInfo);

            DrawBlob.Execute(_deferredQueue, frameInfo, DEPTH_ONLY_PUSH_CONSTANT_INDEX, VkCullModeFlags.Back);

            GraphicsDevice.DeviceAPI.vkCmdEndRendering(frameInfo.CommandBuffer);
        }

        private void DeferredCompositePass(RendererFrameInfo frameInfo)
        {
            _deferredComposite.PushConstantsHandler.SetPushConstantUInt("cameraIndex", 0, (uint)frameInfo.MainCamera);
            _deferredComposite.Dispatch(frameInfo.CommandBuffer, Presenter.FrameIndex, GetGroupCount((uint)MainColourAttachment.Target.Width, 32), GetGroupCount((uint)MainColourAttachment.Target.Height, 32));

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint GetGroupCount(uint threadCount, uint localSize)
        {
            return (threadCount + localSize - 1) / localSize;
        }

        private unsafe void StartDeferredRendering(RendererFrameInfo frameInfo)
        {
            VkCommandBuffer commandBuffer = frameInfo.CommandBuffer;

            VkRenderingAttachmentInfo* colourAttachments = stackalloc VkRenderingAttachmentInfo[]
            {
                G_PositionAttachment.GetAttachmentInfo(),
                G_NormalAttachment.GetAttachmentInfo(),
                G_AlbedoAttachment.GetAttachmentInfo(),
                G_MaskAttachment.GetAttachmentInfo()
            };

            G_PositionAttachment.BeginRenderingMultiAttachment(commandBuffer, 1, colourAttachments, 4, DepthAttachment.GetAttachmentInfo(VkAttachmentLoadOp.Load));

            Presenter.SetToCurrentCameraViewportScissor(commandBuffer);
        }

        public void BeginDepthOnlyRendering(VkCommandBuffer commandBuffer, VkAttachmentLoadOp loadOp)
        {
            DepthAttachment.BeginRenderingOnlyAttachment(commandBuffer, loadOp);
            Presenter.SetToCurrentCameraViewportScissor(commandBuffer);
        }

        public void PostRender()
        {

        }

        public void StartForwardRendering(RendererFrameInfo frameInfo, VkAttachmentLoadOp colourLoad)
        {
            MainColourAttachment.Target.SetImageLayoutAuto(frameInfo.CommandBuffer, VkImageLayout.ColorAttachmentOptimal);
            BrightObjectAttachment.Target.SetImageLayoutAuto(frameInfo.CommandBuffer, VkImageLayout.ColorAttachmentOptimal);
            DepthAttachment.Target.SetImageLayoutAuto(frameInfo.CommandBuffer, VkImageLayout.DepthAttachmentOptimal);
            StartForwardRendering(frameInfo.CommandBuffer, colourLoad);
        }

        public unsafe void StartForwardRendering(VkCommandBuffer commandBuffer, VkAttachmentLoadOp colourLoad, bool onlyMainAttachment = false, bool noDepth = false)
        {

            VkRenderingAttachmentInfo* colourAttachments = stackalloc VkRenderingAttachmentInfo[]
            {
                MainColourAttachment.GetAttachmentInfo(colourLoad),
                BrightObjectAttachment.GetAttachmentInfo(colourLoad),
            };

            MainColourAttachment.BeginRenderingMultiAttachment(commandBuffer, 1, colourAttachments, onlyMainAttachment ? 1 : 2, DepthAttachment.GetAttachmentInfo(VkAttachmentLoadOp.Load));

            Presenter.SetToCurrentCameraViewportScissor(commandBuffer);
        }

        public void EndForwardRendering(RendererFrameInfo frameInfo)
        {
            GraphicsDevice.DeviceAPI.vkCmdEndRendering(frameInfo.CommandBuffer);
        }

        public void BlitFromMainColour(VkCommandBuffer commandBuffer, VkImage dst, int dstWidth, int dstHeight, VkImageAspectFlags dstAspectMask)
        {
            MainColourAttachment.Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.TransferSrcOptimal);

            TextureExtensions.BlitGeneric(commandBuffer, VkFilter.Linear, MainColourAttachment.GetBlitCmd(dstWidth, dstHeight, dstAspectMask), MainColourAttachment.VkImage, MainColourAttachment.CurrentLayout, dst, VkImageLayout.TransferDstOptimal);

            MainColourAttachment.Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.ColorAttachmentOptimal);
        }
        public void BlitFromPostProcessingColour(VkCommandBuffer commandBuffer, VkImage dst, int dstWidth, int dstHeight, VkImageAspectFlags dstAspectMask)
        {
            PostProcessingAttachment.Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.TransferSrcOptimal);

            TextureExtensions.BlitGeneric(commandBuffer, VkFilter.Linear, PostProcessingAttachment.GetBlitCmd(dstWidth, dstHeight, dstAspectMask), PostProcessingAttachment.VkImage, PostProcessingAttachment.CurrentLayout, dst, VkImageLayout.TransferDstOptimal);

            PostProcessingAttachment.Target.SetImageLayoutAuto(commandBuffer, VkImageLayout.ColorAttachmentOptimal);
        }
    }
}
