using Vortice.Vulkan;

namespace VECS
{
    /// <summary>
    /// Managed none-pointer struct used in the creation and configuration of a <see cref="VkGraphicsPipelineCreateInfo"/>
    /// 
    /// A whole load of configuration data for a Vk Graphics Pipeline.
    /// </summary>
    public struct GraphicsPipelineConfigInfo
    {
        public VkVertexInputBindingDescription[] BindingDescriptions;
        public VkVertexInputAttributeDescription[] AttributeDescriptions;
        public VkPipelineViewportStateCreateInfo viewportInfo;
        public VkPipelineInputAssemblyStateCreateInfo inputAssemblyInfo;
        public VkPipelineRasterizationStateCreateInfo rasterizationInfo;
        public VkPipelineMultisampleStateCreateInfo multisampleInfo;
        public VkPipelineColorBlendAttachmentState colourBlendAttachment;
        public VkPipelineColorBlendStateCreateInfo colourBlendInfo;
        public VkPipelineDepthStencilStateCreateInfo depthStencilInfo;
        public VkDynamicState[] dynamicStateEnables;
        public VkPipelineDynamicStateCreateInfo dynamicInfo;
        public VkPipelineLayout pipelineLayout;
        public VkPipelineRenderingCreateInfo pipelineRenderingCreateInfo;
        //public bool dynamicRendering;
        public VkFormat[] colourFormats;
        public VkFormat depthFormat;
        public VkFormat stencilFormat;
        public uint viewMask;
        public GraphicsPipeline BasePipeline;
        public bool AllowDerivative;
        /// <summary>
        /// Default graphics pipeline configuration. Because of course vulkan doesn't have a default.
        /// </summary>
        /// <returns></returns>
        public static GraphicsPipelineConfigInfo DefaultPipelineConfigInfo()
        {

            var attributes = new VertexAttributeDescription[]
            {
                new(VertexAttribute.Position,VertexAttributeFormat.Float3,0,0,0),
                new(VertexAttribute.Normal,VertexAttributeFormat.Float3,12,0,1),
            };

            return DefaultPipelineConfigInfo(MeshExtensions.GetBindingDescription(attributes), MeshExtensions.GetAttributeDescriptions(attributes));
        }


        public static unsafe GraphicsPipelineConfigInfo DefaultPipelineConfigInfo(VkVertexInputBindingDescription[] vkVertexInputBindings, VkVertexInputAttributeDescription[] vkVertexInputAttributes)
        {
            
            VkPipelineColorBlendStateCreateInfo colourBlendInfo = new()
            {
                logicOpEnable = false,
                logicOp = VkLogicOp.Copy,
                attachmentCount = 1,
            };
            colourBlendInfo.blendConstants[0] = 0;
            colourBlendInfo.blendConstants[1] = 0;
            colourBlendInfo.blendConstants[2] = 0;
            colourBlendInfo.blendConstants[3] = 0;

            VkDynamicState[] dynamicStateEnables = [VkDynamicState.Viewport, VkDynamicState.Scissor, VkDynamicState.CullMode];
            VkPipelineDynamicStateCreateInfo dynamicInfo = new()
            {
                dynamicStateCount = (uint)dynamicStateEnables.Length,
                flags = 0
            };
            var attributes = new VertexAttributeDescription[]
            {
                new(VertexAttribute.Position,VertexAttributeFormat.Float3,0,0,0),
                new(VertexAttribute.Normal,VertexAttributeFormat.Float3,12,0,1),
            };
            return new()
            {
                colourFormats = [Presenter.MainColourFormat],
                depthFormat = Presenter.DepthFormat,
                stencilFormat = VkFormat.Undefined,
                pipelineRenderingCreateInfo = new(),
                inputAssemblyInfo = new()
                {
                    topology = VkPrimitiveTopology.TriangleList,
                    primitiveRestartEnable = false
                },

                viewportInfo = new()
                {
                    viewportCount = 1,
                    pViewports = null,
                    scissorCount = 1,
                    pScissors = null
                },

                rasterizationInfo = new()
                {
                    depthClampEnable = false,
                    rasterizerDiscardEnable = false,
                    polygonMode = VkPolygonMode.Fill,
                    lineWidth = 1,
                    cullMode = VkCullModeFlags.Back,
                    frontFace = VkFrontFace.CounterClockwise,
                    depthBiasEnable = false,
                    depthBiasConstantFactor = 0,
                    depthBiasClamp = 0,
                    depthBiasSlopeFactor = 0
                },

                multisampleInfo = new()
                {
                    sampleShadingEnable = false,
                    rasterizationSamples = VkSampleCountFlags.Count1,
                    minSampleShading = 1,
                    pSampleMask = null,
                    alphaToCoverageEnable = false,
                    alphaToOneEnable = false
                },

                colourBlendInfo = colourBlendInfo,

                colourBlendAttachment = new()
                {
                    colorWriteMask = VkColorComponentFlags.All,
                    blendEnable = false,
                    colorBlendOp = VkBlendOp.Add,
                    alphaBlendOp = VkBlendOp.Add,
                    srcAlphaBlendFactor = VkBlendFactor.One,
                    dstAlphaBlendFactor = VkBlendFactor.Zero,
                    srcColorBlendFactor = VkBlendFactor.One,
                    dstColorBlendFactor = VkBlendFactor.Zero,
                },

                depthStencilInfo = new()
                {
                    sType = VkStructureType.PipelineDepthStencilStateCreateInfo,
                    depthTestEnable = true,
                    depthWriteEnable = false,
                    depthCompareOp = VkCompareOp.LessOrEqual,
                    depthBoundsTestEnable = false,
                    minDepthBounds = 0.0f,
                    maxDepthBounds = 1.0f,
                    stencilTestEnable = false,
                    front = default,
                    back = default
                },

                dynamicStateEnables = dynamicStateEnables,

                dynamicInfo = dynamicInfo,

                BindingDescriptions = vkVertexInputBindings,
                AttributeDescriptions = vkVertexInputAttributes
            };
        }

        public static GraphicsPipelineConfigInfo DefaultPipelineConfigInfo(VkPipelineLayout pipelineLayout)
        {
            var pipelineConfigInfo = DefaultPipelineConfigInfo();
            pipelineConfigInfo.pipelineLayout = pipelineLayout;

            return pipelineConfigInfo;
        }

        /// <summary>
        /// Modify the given configInfo to enable alpha blending of the colour channel.
        /// </summary>
        /// <param name="configInfo">Configuration to modify</param>
        public static void EnableAlphaBlending(ref GraphicsPipelineConfigInfo configInfo)
        {
            var colourBlendAttachment = configInfo.colourBlendAttachment;

            colourBlendAttachment.colorWriteMask = VkColorComponentFlags.All;
            colourBlendAttachment.blendEnable = true;

            colourBlendAttachment.colorBlendOp = VkBlendOp.Add;
            colourBlendAttachment.alphaBlendOp = VkBlendOp.Add;

            colourBlendAttachment.srcColorBlendFactor = VkBlendFactor.SrcAlpha;
            colourBlendAttachment.dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha;

            colourBlendAttachment.srcAlphaBlendFactor = VkBlendFactor.One;
            colourBlendAttachment.dstAlphaBlendFactor = VkBlendFactor.Zero;

            configInfo.colourBlendAttachment = colourBlendAttachment;
        }
    }
}
