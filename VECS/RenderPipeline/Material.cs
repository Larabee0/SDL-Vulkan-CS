using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VECS.LowLevel;
using Vortice.Vulkan;

namespace VECS
{
    public class Material : DisposableAsset
    {
        internal class TemporaryDescriptor : IDisposable
        {
            public DescriptorBuffer DescriptorBuffer;
            public unsafe byte* _hostBuffer;

            public unsafe TemporaryDescriptor(DescriptorSetInfo setInfo)
            {
                DescriptorBuffer = new(setInfo.DescriptorBuffers[0].Layout, setInfo._descriptorBindings, (int)setInfo._uniformCount, setInfo.StorageBufferCount > 0 || setInfo.UnifromBufferSize > 0, setInfo.ImageCount > 0);


                var totalallocationSize = DescriptorBuffer.AllocationSize;

                _hostBuffer = (byte*)NativeMemory.AlignedAlloc(totalallocationSize, (uint)GPUBufferExtensions.GetAlignment(DescriptorBuffer.AlignedSize));

                NativeMemory.Fill(_hostBuffer, totalallocationSize, 0);
                DescriptorBuffer.SetHostPtr(_hostBuffer);
            }

            public unsafe void Dispose()
            {
                GC.SuppressFinalize(this);
                DescriptorBuffer.Dispose();
                NativeMemory.AlignedFree(_hostBuffer);
                _hostBuffer = null;
                GC.ReRegisterForFinalize(this);
            }
        }

        private readonly uint _variantIndex;
        private readonly GraphicsPipeline _graphicsPipeline;
        private Vector2ULong[][] _storageBufferRegions;
        private ITextureProvider[][] _textures;

        /// this allocation will be an offset into <see cref="GraphicsPipeline._uniformBuffer"> host ptr, unless the material is new, which case the allocation is temporarily local.
        /// it will be copied into the <see cref="GraphicsPipeline._uniformBuffer"> host ptr during the shader set variant allocation phase with the local allocation being freed
        /// and replaced with the offset ptr.
        internal unsafe void* pUniformBuffer;
        internal bool localUniformAllocation;
        internal TemporaryDescriptor[] localDescriptors;
        internal GPUBuffer localUniformBuffer;

        public VkCullModeFlags CullMode = VkCullModeFlags.None;
        public bool OverrideCullMode = false;
        public bool AlphaClipping = false;
        public float AlphaCutoff = 0.5f;
        public Texture2D AlphaTexture;

        public uint VariantIndex => _variantIndex;
        public int TotalSets => DescriptorSetCount;
        public int DescriptorSetCount => _graphicsPipeline.DescriptorSetCount;
        public DescriptorSetInfo[] DescriptorSetInfos => _graphicsPipeline.DescriptorSetInfos;
        public GraphicsPipeline Pipeline => _graphicsPipeline;
        public PushConstantsHandler PushConstants => _graphicsPipeline.PushConstants;

        public ulong CombinedHash => CombineMaterial(_graphicsPipeline.Hash, (int)_variantIndex);

        internal unsafe Material(string name, GraphicsPipeline pipeline, bool localUniformAlloc = true)
        {
            AssetName = pipeline.AssetName + '.' + name;
            _variantIndex = pipeline.GetNextVariantIndex();
            _graphicsPipeline = pipeline;

            _textures = new ITextureProvider[DescriptorSetCount][];
            _storageBufferRegions = new Vector2ULong[DescriptorSetCount][];

            if (localUniformAlloc && pipeline.UniformBufferSize > 0)
            {
                localUniformAllocation = true;
                localUniformBuffer = new(pipeline.UniformBufferSize, 1, VkBufferUsageFlags.UniformBuffer, true, false, false);
                pUniformBuffer = localUniformBuffer._hostPtr;
            }
            else
            {
                pUniformBuffer = null;
                localUniformAllocation = false;
            }

            if(_variantIndex > 0)
            {
                CreateTemporaryDescriptor();
            }

            for (uint i = 0; i < TotalSets; i++)
            {
                var setInfo = DescriptorSetInfos[i];
                if (setInfo.StorageBufferCount > 0 && !setInfo.NoAllocStorageBuffers)
                {
                    _storageBufferRegions[i] = new Vector2ULong[setInfo.StorageBufferCount];

                    for (uint j = 0; j < setInfo.StorageBufferCount; j++)
                    {
                        _storageBufferRegions[i][j] = new(0, Vulkan.VK_WHOLE_SIZE);
                    }
                }
                if (setInfo.ImageCount > 0)
                {
                    _textures[i] = new ITextureProvider[setInfo.ImageCount];

                    for (int j = 0; j < setInfo.BindingCount; j++)
                    {
                        DescriptorBinding binding = setInfo._descriptorBindings[j];
                        if (!binding.Image) continue;
                        var imageIndex = setInfo.BindPointToImageIndex[binding.BindPoint];
                        var engineTexture = EngineTextures.TryGetTexture(binding.Id);
                        if (engineTexture != null)
                        {
                            _textures[i][imageIndex] = engineTexture;
                        }
                        else if(binding.VkSetLayoutBinding.descriptorCount > 1)
                        {
                            var fill = _textures[i][imageIndex] = new BindingArrayTexture((int)binding.VkSetLayoutBinding.descriptorCount);

                            for (int k = 0; k < fill.ImageCount; k++)
                            {
                                fill.SetTexture(EngineTextures.MissingTexture, k);
                            }
                        }
                        else
                        {
                            _textures[i][imageIndex] = (SingleTexture)EngineTextures.MissingTexture;
                        }
                        WriteTexturesToDescriptorBuffer(binding.DescriptorSetIndex, binding.BindPoint);
                    }
                }
            }

            _graphicsPipeline.AddVariant(this);
            AssetDataBase<Material>.Add(this);
        }

        private void CreateTemporaryDescriptor()
        {
            localDescriptors = new TemporaryDescriptor[TotalSets];

            for (int i = 0; i < TotalSets; i++)
            {
                DescriptorSetInfo setInfo = DescriptorSetInfos[i];
                localDescriptors[i] = new(setInfo);
            }

            WriteUniformToDescriptorBuffers();
        }

        private void WriteUniformToDescriptorBuffers()
        {
            if (!Pipeline.HasUniforms) return;

            for (uint i = 0; i < DescriptorSetCount; i++)
            {
                var setInfo = DescriptorSetInfos[i];
                var descriptorBuffer = localDescriptors[i].DescriptorBuffer;
                if (Pipeline.MeshShaderDescriptorSetIndex == i) continue;

                for (uint j = 0; j < setInfo.BindingCount; j++)
                {
                    var binding = setInfo._descriptorBindings[j];

                    if (!binding.UniformBuffer) continue;

                    var internalOffset = Pipeline.InternalUniformBufferOffset(binding.DescriptorSetIndex, binding.BindPoint);
                    var global = EngineBuffers.TryGetBuffer(binding.Id);

                    VkDescriptorAddressInfoEXT addressRange;
                    if (global != null)
                    {
                        addressRange = global[0].GetBufferAddressRangeBytes();
                    }
                    else
                    {
                        addressRange = localUniformBuffer.GetBufferAddressRangeBytes(internalOffset, binding.BufferSize);
                    }

                    descriptorBuffer.SetBufferBinding(addressRange, binding.DescriptorType, 0, binding.BindPoint);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Bind(in RendererFrameInfo frameInfo)
        {
            Pipeline.BindAll(frameInfo, _variantIndex);
        }

        public unsafe void BindCareful(in RendererFrameInfo frameInfo)
        {
            int frameIndex = Presenter.FrameIndex;
            VkDescriptorBufferBindingInfoEXT* bindingInfo = stackalloc VkDescriptorBufferBindingInfoEXT[DescriptorSetCount];
            ulong* offsets = stackalloc ulong[DescriptorSetCount];
            uint* indices = stackalloc uint[DescriptorSetCount];

            if (localDescriptors == null)
            {
                for (uint i = 0; i < DescriptorSetCount; i++)
                {
                    DescriptorSetInfo descriptorSetInfo = DescriptorSetInfos[i];
                    DescriptorBuffer buffer = descriptorSetInfo.DescriptorBuffers[frameIndex];

                    bindingInfo[i] = buffer.BindingInfo;
                    offsets[i] = buffer.AlignedSize * VariantIndex;
                    indices[i] = i;
                }
            }
            else
            {
                if (localUniformAllocation)
                {
                    GPUBufferExtensions.WriteFromHostDelayed(localUniformBuffer, 0, Vulkan.VK_WHOLE_SIZE);
                }
                for (int i = 0; i < DescriptorSetCount; i++)
                {
                    DescriptorSetInfo descriptorSetInfo = DescriptorSetInfos[i];
                    
                    DescriptorBuffer buffer = localDescriptors[i].DescriptorBuffer;
                    buffer.Flush();
                    
                    bindingInfo[i] = buffer.BindingInfo;
                    offsets[i] = 0;
                    indices[i] = (uint)i;
                }
            }

            Pipeline.BindPipe(frameInfo.CommandBuffer);

            if (OverrideCullMode)
            {
                GraphicsDevice.DeviceAPI.vkCmdSetCullMode(frameInfo.CommandBuffer, CullMode);
            }

            DescriptorBuffer.BindSets(frameInfo.CommandBuffer, (uint)DescriptorSetCount, bindingInfo);
            DescriptorBuffer.SetOffsets(frameInfo.CommandBuffer, Pipeline.PipelineLayout, VkPipelineBindPoint.Graphics, 0, (uint)DescriptorSetCount, offsets, indices);
        }

        public void SetSampler(uint setIndex, uint bindPoint, TextureSampler sampler)
        {

            for (int f = 0; f < SwapChain.MAX_CONCURRENT_FRAMES; f++)
            {
                var descriptorBuffer = GetDescriptorBuffer(setIndex, f);
                SetSampler(descriptorBuffer, sampler._textureSampler, bindPoint, setIndex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTexture(uint setIndex, uint bindPoint, Texture texture, int index = 0)
        {
            int imageIndex = DescriptorSetInfos[setIndex].BindPointToImageIndex[bindPoint];
            if (_textures[setIndex][imageIndex].First == texture) return;
            if (_textures[setIndex][imageIndex].SetTexture(texture, index))
            {
                WriteTexturesToDescriptorBuffer(setIndex, bindPoint);
            }
            
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTexture(uint setIndex, uint bindPoint, ITextureProvider textures)
        {
            int imageIndex = DescriptorSetInfos[setIndex].BindPointToImageIndex[bindPoint];
            bool writeDescriptorNow = false;
            for (int i = 0; i < textures.ImageCount; i++)
            {
                writeDescriptorNow |= _textures[setIndex][imageIndex].SetTexture(textures.GetTexture(i), i);
            }
            if (writeDescriptorNow)
            {
                WriteTexturesToDescriptorBuffer(setIndex, bindPoint);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DescriptorBuffer GetDescriptorBuffer(uint setIndex, int frameIndex)
        {
            if (localDescriptors != null)
            {
                return localDescriptors[setIndex].DescriptorBuffer;
            }
            else
            {
                return DescriptorSetInfos[setIndex].DescriptorBuffers[frameIndex];
            }
        }

        private unsafe void WriteTexturesToDescriptorBuffer(uint setIndex, uint bindPoint)
        {
            int imageIndex = DescriptorSetInfos[setIndex].BindPointToImageIndex[bindPoint];
            ITextureProvider textures = _textures[setIndex][imageIndex];
            uint variant = localDescriptors != null ? 0 : VariantIndex;

            VkDescriptorImageInfo* imageInfos = stackalloc VkDescriptorImageInfo[textures.ImageCount];

            for (int i = 0; i < textures.ImageCount; i++)
            {
                imageInfos[i] = textures.GetTexture(i).ImageInfo;
            }

            for (int f = 0; f < SwapChain.MAX_CONCURRENT_FRAMES; f++)
            {
                var descriptorBuffer = GetDescriptorBuffer(setIndex,f);
                SetTextures(descriptorBuffer, DescriptorSetInfos[setIndex].GetBinding(bindPoint).DescriptorType, imageInfos, (uint)textures.ImageCount, bindPoint, variant);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void SetTextures(DescriptorBuffer buffer, VkDescriptorType descriptorType, VkDescriptorImageInfo* imageInfos, uint imageCount, uint bindingIndex,  uint variant)
        {
            buffer.SetImageInfoBinding(imageInfos, imageCount, descriptorType, variant, bindingIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetSampler(DescriptorBuffer buffer, VkSampler sampler, uint bindingIndex, uint variant)
        {
            buffer.SetSamplerBinding(sampler, variant, bindingIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetStorageBufferLength(uint setIndex, uint bindPoint, uint offset, uint length)
        {
            var bufferIndex = DescriptorSetInfos[setIndex].BindPointToBufferIndex[bindPoint];
            Vector2ULong newRegion = new(offset, length);
            if (_storageBufferRegions[setIndex][bufferIndex] == newRegion)
            {
                return false;
            }

            _storageBufferRegions[setIndex][bufferIndex] = newRegion;
            WriteStorageBuffer(setIndex, bindPoint);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteStorageBuffer(uint setIndex, uint bindPoint)
        {
            var bufferIndex = DescriptorSetInfos[setIndex].BindPointToBufferIndex[bindPoint];
            var region = _storageBufferRegions[setIndex][bufferIndex];
            uint variant = localDescriptors != null ? 0 : VariantIndex;
            for (int f = 0; f < SwapChain.MAX_CONCURRENT_FRAMES; f++)
            {
                var descriptorBuffer = GetDescriptorBuffer(setIndex, f);
                descriptorBuffer.SetStorageBinding(DescriptorSetInfos[setIndex].GetBufferAddressInfo(f, bufferIndex, region.X, region.Y), variant, bindPoint);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong GetStorageBufferLength(uint setIndex, uint bindPoint)
        {
            var bufferIndex = DescriptorSetInfos[setIndex].BindPointToBufferIndex[bindPoint];
            var region = _storageBufferRegions[setIndex][bufferIndex];
            return region.X + region.Y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SwapChainBuffer GetStorageSwapChainBuffer(int propertyId)
        {
            return _graphicsPipeline.GetStorageSwapChainBuffer(propertyId);
        }

        public unsafe void ExecuteDrawCommands(RendererFrameInfo frameInfo, VkCommandBuffer commandBuffer, Span<MaterialDrawCommand> drawCmds, int drawCount, SwapChainBuffer<VECSDrawIndexIndirectCommand> indirectCmdBuffer)
        {
            if (drawCount <= 0) return;
            var frameIndex = Presenter.FrameIndex;
            var command = drawCmds[0];
            command.Variant = (int)VariantIndex;
            if (Pipeline._preBindUpdate)
            {
                GraphicsPipeline.Update(Pipeline);
            }
            VkDescriptorBufferBindingInfoEXT* bindingInfo = stackalloc VkDescriptorBufferBindingInfoEXT[DescriptorSetCount];
            ulong* offsets = stackalloc ulong[DescriptorSetCount];
            uint* indices = stackalloc uint[DescriptorSetCount];
            for (uint i = 0; i < DescriptorSetCount; i++)
            {
                DescriptorSetInfo descriptorSetInfo = DescriptorSetInfos[i];
                DescriptorBuffer buffer = descriptorSetInfo.DescriptorBuffers[frameIndex];

                bindingInfo[i] = buffer.BindingInfo;
                offsets[i] = buffer.AlignedSize * (uint)command.Variant;
                indices[i] = i;
            }

            Pipeline.BindPipe(commandBuffer);
            
            if (OverrideCullMode)
            {
                GraphicsDevice.DeviceAPI.vkCmdSetCullMode(commandBuffer, CullMode);
            }

            DescriptorBuffer.BindSets(commandBuffer, (uint)DescriptorSetCount, bindingInfo);
            DescriptorBuffer.SetOffsets(commandBuffer, Pipeline.PipelineLayout, VkPipelineBindPoint.Graphics, 0, (uint)DescriptorSetCount, offsets, indices);
            var lastVariant = (int)VariantIndex;
            for (int i = 0; i < drawCount; i++)
            {
                command = drawCmds[i];
                command.Variant = (int)VariantIndex;
                Pipeline.ExecuteDrawCommand(commandBuffer, frameIndex, command.Entity, indirectCmdBuffer, command, offsets, indices, ref lastVariant);
            }
        }

        public unsafe void ExecuteDrawCommandsPushConstantOverride(RendererFrameInfo frameInfo, int pushConstantOverride, VkCommandBuffer commandBuffer, Span<MaterialDrawCommand> drawCmds, int drawCount, SwapChainBuffer<VECSDrawIndexIndirectCommand> indirectCmdBuffer)
        {
            if (drawCount <= 0) return;
            var frameIndex = Presenter.FrameIndex;

            var command = drawCmds[0];
            command.Variant = (int)VariantIndex;
            if (Pipeline._preBindUpdate)
            {
                GraphicsPipeline.Update(Pipeline);
            }
            VkDescriptorBufferBindingInfoEXT* bindingInfo = stackalloc VkDescriptorBufferBindingInfoEXT[DescriptorSetCount];
            ulong* offsets = stackalloc ulong[DescriptorSetCount];
            uint* indices = stackalloc uint[DescriptorSetCount];

            for (uint i = 0; i < DescriptorSetCount; i++)
            {
                DescriptorSetInfo descriptorSetInfo = DescriptorSetInfos[i];
                DescriptorBuffer buffer = descriptorSetInfo.DescriptorBuffers[frameIndex];

                bindingInfo[i] = buffer.BindingInfo;
                offsets[i] = buffer.AlignedSize * (uint)command.Variant;
                indices[i] = i;
            }
            Pipeline.BindPipe(commandBuffer);
            DescriptorBuffer.BindSets(commandBuffer, (uint)DescriptorSetCount, bindingInfo);
            DescriptorBuffer.SetOffsets(commandBuffer, Pipeline.PipelineLayout, VkPipelineBindPoint.Graphics, 0, (uint)DescriptorSetCount, offsets, indices);

            if (OverrideCullMode)
            {
                GraphicsDevice.DeviceAPI.vkCmdSetCullMode(commandBuffer, CullMode);
            }

            var lastVariant = (int)VariantIndex;
            for (int i = 0; i < drawCount; i++)
            {
                command = drawCmds[i];
                command.Variant = (int)VariantIndex;
                Pipeline.ExecuteDrawCommand(commandBuffer, frameIndex, pushConstantOverride, indirectCmdBuffer, command, offsets, indices, ref lastVariant);
            }
        }

        public void Reinitialise(Dictionary<int, Vector4UInt> textureRemap, Dictionary<int, Vector4UInt> storageRemap)
        {
            var existingImages = _textures;
            var existingRegions = _storageBufferRegions;

            _textures = new ITextureProvider[DescriptorSetCount][];
            _storageBufferRegions = new Vector2ULong[DescriptorSetCount][];
            for (uint i = 0; i < TotalSets; i++)
            {
                var setInfo = DescriptorSetInfos[i];
                if (setInfo.StorageBufferCount > 0 && !setInfo.NoAllocStorageBuffers)
                {
                    _storageBufferRegions[i] = new Vector2ULong[setInfo.StorageBufferCount];

                    for (uint j = 0; j < setInfo.StorageBufferCount; j++)
                    {
                        _storageBufferRegions[i][j] = new(0, Vulkan.VK_WHOLE_SIZE);
                    }
                }
                if (setInfo.ImageCount > 0)
                {
                    _textures[i] = new ITextureProvider[setInfo.ImageCount];

                    for (int j = 0; j < setInfo.BindingCount; j++)
                    {
                        DescriptorBinding binding = setInfo._descriptorBindings[j];
                        if (!binding.Image) continue;

                        if(textureRemap.TryGetValue(binding.Id,out var remapIndices))
                        {
                            _textures[remapIndices.Z][remapIndices.W] = existingImages[remapIndices.X][remapIndices.Y];
                            WriteTexturesToDescriptorBuffer(binding.DescriptorSetIndex, binding.BindPoint);
                            continue;
                        }

                        var imageIndex = setInfo.BindPointToImageIndex[binding.BindPoint];
                        var engineTexture = EngineTextures.TryGetTexture(binding.Id);
                        if (engineTexture != null)
                        {
                            _textures[i][imageIndex] = engineTexture;
                        }
                        else if (binding.VkSetLayoutBinding.descriptorCount > 1)
                        {
                            var fill = _textures[i][imageIndex] = new BindingArrayTexture((int)binding.VkSetLayoutBinding.descriptorCount);

                            for (int k = 0; k < fill.ImageCount; k++)
                            {
                                fill.SetTexture(EngineTextures.MissingTexture, k);
                            }
                        }
                        else
                        {
                            _textures[i][imageIndex] = (SingleTexture)EngineTextures.MissingTexture;
                        }
                        WriteTexturesToDescriptorBuffer(binding.DescriptorSetIndex, binding.BindPoint);
                    }
                }
            }

            for (int i = 0; i < TotalSets; i++)
            {
                var setInfo = DescriptorSetInfos[i];

                for (int j = 0; j < setInfo.BindingCount; j++)
                {
                    if (storageRemap.TryGetValue(setInfo._descriptorBindings[j].Id, out var remapIndices) && remapIndices.Z != uint.MaxValue && remapIndices.W != uint.MaxValue)
                    {
                        _storageBufferRegions[remapIndices.Z][remapIndices.W] = existingRegions[remapIndices.X][remapIndices.Y];
                    }
                }
            }
        }

        public override unsafe void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            GC.SuppressFinalize(this);
            Pipeline.RemoveVariant(this);
            if (localUniformAllocation)
            {
                localUniformBuffer.Dispose();
                localUniformBuffer = null;
                localUniformAllocation = false;
            }
            if (localDescriptors != null)
            {
                for (int i = 0; i < localDescriptors.Length; i++)
                {
                    localDescriptors[i]?.Dispose();
                }
                localDescriptors = null;
            }
            pUniformBuffer = null;

            GC.ReRegisterForFinalize(this);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RewriteDescriptors(Material variant)
        {
            for (uint setIndex = 0; setIndex < variant.TotalSets; setIndex++)
            {
                for (int i = 0; i < variant.DescriptorSetInfos[setIndex].BindingCount; i++)
                {
                    var binding = variant.DescriptorSetInfos[setIndex]._descriptorBindings[i];
                    if (binding.StorageBuffer)
                    {
                        variant.WriteStorageBuffer(setIndex, binding.BindPoint);
                    }
                    if (binding.Image)
                    {
                        variant.WriteTexturesToDescriptorBuffer(setIndex, binding.BindPoint);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong CombineMaterial(int pipelineHash, int materialHash)
        {
            unchecked
            {
                ulong output = (uint)pipelineHash;
                return (output << 32) + (uint)materialHash;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DecomposeHash(ulong combined, out int pipelineHash, out int materialHash)
        {
            unchecked
            {
                pipelineHash = (int)(uint)(combined >> 32);
                materialHash = (int)(uint)(combined + (0 << 32));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int DecodeIndex(ulong combined)
        {
            unchecked
            {
                return (int)(uint)(combined + (0 << 32));
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int DecodePipelineHash(ulong combined)
        {
            unchecked
            {
                return (int)(uint)(combined >> 32);
            }
        }
    }
}
