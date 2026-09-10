using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VECS.LowLevel;
using Vortice.Vulkan;

namespace VECS
{
    public class ComputeVariant : DisposableAsset
    {
        internal unsafe void* pUniformBuffer;
        internal bool localUniformAllocation;

        private readonly ComputePipeline _computePipeline;
        private readonly uint _variantIndex;
        private ITextureProvider[][] _textures;
        private GPUBuffer[][][] _storageBuffers;

        private GPUBuffer _tempUniformBuffer;
        private DescriptorSetInfo[] _tempDescriptorSetInfos;
        internal bool _allowTmpBufferAllocation;

        public uint VariantIndex => _variantIndex;
        public int TotalSets => DescriptorSetCount;
        public int DescriptorSetCount => _computePipeline.DescriptorSetCount;
        public DescriptorSetInfo[] DescriptorSetInfos => _computePipeline.DescriptorSetInfos;
        public ComputePipeline Pipeline => _computePipeline;
        public PushConstantsHandler PushConstantsHandler => _computePipeline.PushConstants;

        internal unsafe ComputeVariant(string name, ComputePipeline pipeline, bool localUniformAlloc = true, bool allowTmpBufferAllocation = true)
        {
            AssetName = pipeline.AssetName + '.' + name;
            _variantIndex = pipeline.GetNextVariantIndex();
            _computePipeline = pipeline;
            _allowTmpBufferAllocation = allowTmpBufferAllocation;
            localUniformAllocation = localUniformAlloc && pipeline.UniformBufferSize > 0;


            if (localUniformAlloc && allowTmpBufferAllocation && pipeline.UniformBufferSize > 0)
            {
                AllocateTemporaryBuffers();
            }
            else
            {
                pUniformBuffer = null;
            }

            _textures = new ITextureProvider[DescriptorSetCount][];
            _storageBuffers = new GPUBuffer[DescriptorSetCount][][];
            for (int i = 0; i < DescriptorSetCount; i++)
            {
                if (DescriptorSetInfos[i].HasStorageBuffers)
                {
                    _storageBuffers[i] = new GPUBuffer[DescriptorSetInfos[i].StorageBufferCount][];
                    
                    for (int j = 0; j < DescriptorSetInfos[i].BindingCount; j++)
                    {
                        var binding = DescriptorSetInfos[i]._descriptorBindings[j];
                        if (!binding.StorageBuffer) continue;
                        var bufferIndex = DescriptorSetInfos[i].BindPointToBufferIndex[binding.BindPoint];
                        _storageBuffers[i][bufferIndex] = new GPUBuffer[SwapChain.MAX_CONCURRENT_FRAMES];
                        var engineBuffer = EngineBuffers.TryGetBuffer(binding.Id);
                        if(engineBuffer != null)
                        {
                            SetStorageBuffer(binding.Id, engineBuffer);
                        }
                        else
                        {
                            SetStorageBuffer(binding.Id, DescriptorSetInfos[i].StorageBuffers[bufferIndex]);
                        }
                    }
                }

                if (DescriptorSetInfos[i].HasImages)
                {
                    _textures[i] = new ITextureProvider[DescriptorSetInfos[i].ImageCount];
                    for (int j = 0; j < DescriptorSetInfos[i].BindingCount; j++)
                    {
                        var binding = DescriptorSetInfos[i]._descriptorBindings[j];
                        if (!binding.Image) continue;
                        var imageIndex = DescriptorSetInfos[i].BindPointToImageIndex[binding.BindPoint];
                        var engineTexture = EngineTextures.TryGetTexture(binding.Id);
                        if (engineTexture != null)
                        {
                            if (binding.VkSetLayoutBinding.descriptorCount > 1)
                            {
                                _textures[i][imageIndex] = new BindingArrayTexture((int)binding.VkSetLayoutBinding.descriptorCount);
                            }
                            else
                            {
                                _textures[i][imageIndex] = new SingleTexture(null);
                            }
                            SetTextures(binding.DescriptorSetIndex, binding.BindPoint, engineTexture);
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

            _computePipeline.AddVariant(this);
            AssetDataBase<ComputeVariant>.Add(this);
        }

        public unsafe void AllocateTemporaryBuffers()
        {
            if (!localUniformAllocation) return;
            if (!_allowTmpBufferAllocation) return;

            _tempDescriptorSetInfos = _computePipeline.GetTemporaryDescriptorSetInfos();
            _tempUniformBuffer = new(_computePipeline.UniformBufferSize, 1, _computePipeline.UniformFlags, true, false, false);
            pUniformBuffer = _tempUniformBuffer.HostPtr;
        }

        public void DiposeTemporaryBuffers()
        {
            _tempUniformBuffer?.EnqueueForDisposal();
            if(_tempDescriptorSetInfos != null)
            {
                for (int i = 0; i < _tempDescriptorSetInfos.Length; i++)
                {
                    _tempDescriptorSetInfos[i]?.Dispose();
                }
                _tempDescriptorSetInfos = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool LookUpProperty(int propertyId, out ShaderProperty propertyInfo)
        {
            return _computePipeline.LookUpProperty(propertyId, out propertyInfo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetUInt(int propertyId, uint value)
        {
            WriteToBuffer(propertyId, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetInt(int propertyId, int value)
        {
            WriteToBuffer(propertyId, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFloat(int propertyId, float value)
        {
            WriteToBuffer(propertyId, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVector2(int propertyId, Vector2 value)
        {
            WriteToBuffer(propertyId, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVector4(int propertyId, Vector4 value)
        {
            WriteToBuffer(propertyId, value);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetUniform<T>(int propertyId, T value) where T : unmanaged
        {
            WriteToBuffer(propertyId, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteToBuffer<T>(int propertyId, T value) where T : unmanaged
        {
            if (LookUpProperty(propertyId, out var propertyInfo))
            {
                WriteToBuffer(propertyInfo, value);
            }
        }
        public unsafe void WriteToBuffer<T>(ShaderProperty propertyInfo, T element) where T : unmanaged
        {
            _computePipeline.WriteToUniformBuffer(pUniformBuffer, propertyInfo,element);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DescriptorSetInfo GetDescriptorInfo(uint setIndex)
        {
            if (localUniformAllocation)
            {
                return _tempDescriptorSetInfos[setIndex];
            }
            else
            {
                return DescriptorSetInfos[setIndex];
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetStorageBuffer(int propertyId, SwapChainBuffer buffer)
        {
            SetStorageBuffer(propertyId, buffer, 0, Vulkan.VK_WHOLE_SIZE);
        }

        public void SetStorageBuffer(int propertyId, SwapChainBuffer buffer, ulong offset, ulong count)
        {
            if (buffer == null || buffer.IsDisposed) return;
            if (LookUpProperty(propertyId, out var propertyInfo) && propertyInfo.BindingInfo.StorageBuffer)
            {
                SetStorageBuffer(offset, count, buffer, propertyInfo.SetIndex, propertyInfo.BindPoint);
            }
        }


        private void SetStorageBuffer(ulong offset, ulong count, SwapChainBuffer buffer, uint setIndex, uint bindPoint)
        {
            var setInfo = GetDescriptorInfo(setIndex);
            uint variant = localUniformAllocation ? 0 : VariantIndex;
            var bufferArray = _storageBuffers[setIndex][setInfo.BindPointToBufferIndex[bindPoint]];
            for (int i = 0; i < SwapChain.MAX_CONCURRENT_FRAMES; i++)
            {
                bufferArray[i] = buffer[i];
                setInfo.WriteDescriptors(i, bindPoint, variant, buffer[i], offset, count);
            }
        }

        public void SetStorageBuffer(int propertyId, GPUBuffer buffer)
        {
            SetStorageBuffer(propertyId, buffer, 0, Vulkan.VK_WHOLE_SIZE);
        }

        public void SetStorageBuffer(int propertyId, GPUBuffer buffer, ulong offset, ulong count)
        {
            if (buffer == null || buffer.IsDisposed) return;
            if (LookUpProperty(propertyId, out var propertyInfo) && propertyInfo.BindingInfo.StorageBuffer)
            {
                var setInfo = GetDescriptorInfo(propertyInfo.SetIndex);
                uint variant = localUniformAllocation ? 0 : VariantIndex;
                var bufferArray = _storageBuffers[propertyInfo.SetIndex][setInfo.BindPointToBufferIndex[propertyInfo.BindPoint]];
                for (int i = 0; i < SwapChain.MAX_CONCURRENT_FRAMES; i++)
                {
                    bufferArray[i] = buffer;
                    setInfo.WriteDescriptors(i, propertyInfo.BindPoint, variant, buffer,offset,count);
                }
            }
        }

        private void WriteStorageBuffersToDescriptorBuffer(uint setIndex, uint bindingIndex)
        {
            int bufferIndex = DescriptorSetInfos[setIndex].BindPointToBufferIndex[bindingIndex];
            var buffers = _storageBuffers[setIndex][bufferIndex];
            
            var setInfo = GetDescriptorInfo(setIndex);
            uint variant = localUniformAllocation ? 0 : VariantIndex;

            for (int i = 0; i < SwapChain.MAX_CONCURRENT_FRAMES; i++)
            {
                if (buffers[i] == null || buffers[i].IsDisposed) continue;
                setInfo.WriteDescriptors(i, bindingIndex, variant, buffers[i]);
            }
        }

        private unsafe void WriteTexturesToDescriptorBuffer(uint setIndex, uint bindPoint)
        {
            int imageIndex = DescriptorSetInfos[setIndex].BindPointToImageIndex[bindPoint];
            ITextureProvider textures = _textures[setIndex][imageIndex];
            if (textures.AnyDisposed) return;
            var setInfo = GetDescriptorInfo(setIndex);
            uint variant = localUniformAllocation ? 0 : VariantIndex;

            VkDescriptorImageInfo* imageInfos = stackalloc VkDescriptorImageInfo[textures.ImageCount];

            for (int i = 0; i < textures.ImageCount; i++)
            {
                imageInfos[i] = textures.GetTexture(i).ImageInfo;
            }

            SetTextures(bindPoint, setInfo, variant, imageInfos, (uint)textures.ImageCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void SetTextures(uint bindPoint, DescriptorSetInfo setInfo, uint variant, VkDescriptorImageInfo* imageInfos, uint imageCount)
        {
            for (int i = 0; i < SwapChain.MAX_CONCURRENT_FRAMES; i++)
            {
                setInfo.WriteDescriptors(i, bindPoint, variant, imageInfos, imageCount, setInfo.GetBinding(bindPoint).DescriptorType);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void SetTexturesUnsafe(int propertyId, VkDescriptorImageInfo* imageInfos, uint imageCount)
        {
            if (LookUpProperty(propertyId, out var propertyInfo) && propertyInfo.BindingInfo.Image)
            {
                var setInfo = GetDescriptorInfo(propertyInfo.SetIndex);
                uint variant = localUniformAllocation ? 0 : VariantIndex;
                SetTextures(propertyInfo.BindPoint, setInfo, variant, imageInfos, imageCount);
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTextures(int propertyId, ITextureProvider textureProvider)
        {
            if (textureProvider == null) return;
            if (LookUpProperty(propertyId, out var propertyInfo) && propertyInfo.BindingInfo.Image)
            {
                SetTextures(propertyInfo.SetIndex, propertyInfo.BindPoint, textureProvider);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTextures(uint setIndex, uint bindPoint, ITextureProvider textures)
        {
            if (textures == null) return;
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
        public void SetTexture(int propertyId, Texture texture)
        {
            if (LookUpProperty(propertyId, out var propertyInfo) && propertyInfo.BindingInfo.Image)
            {
                SetTexture(propertyInfo.SetIndex, propertyInfo.BindPoint, texture);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTexture(uint setIndex, uint bindPoint, Texture texture, int index = 0)
        {
            int imageIndex = DescriptorSetInfos[setIndex].BindPointToImageIndex[bindPoint];

            if (_textures[setIndex][imageIndex].First == texture) return;
            if(_textures[setIndex][imageIndex].SetTexture(texture, index))
            {
                WriteTexturesToDescriptorBuffer(setIndex, bindPoint);
            }
        }

        private void WriteUniformToDescriptorBuffers()
        {
            if (!_computePipeline.HasUniforms) return;
            for (uint i = 0; i < DescriptorSetCount; i++)
            {
                var setInfo = _tempDescriptorSetInfos[i];

                for (uint j = 0; j < setInfo.BindingCount; j++)
                {
                    var binding = setInfo._descriptorBindings[j];

                    if (!binding.UniformBuffer) continue;

                    var internalOffset = _computePipeline.InternalUniformBufferOffset(binding.DescriptorSetIndex, binding.BindPoint);
                    var global = EngineBuffers.TryGetBuffer(binding.Id);
                    VkDescriptorAddressInfoEXT addressRange;
                    if (global != null)
                    {
                        addressRange = global[0].GetBufferAddressRangeBytes();
                    }
                    else
                    {
                        addressRange = _tempUniformBuffer.GetBufferAddressRangeBytes(internalOffset, binding.BufferSize);
                    }

                    for (int frameIndex = 0; frameIndex < SwapChain.MAX_CONCURRENT_FRAMES; frameIndex++)
                    {
                        setInfo.DescriptorBuffers[frameIndex].SetBufferBinding(addressRange, binding.DescriptorType, 0, binding.BindPoint);
                    }
                }
            }
        }

        public unsafe void Dispatch(VkCommandBuffer commandBuffer, int frameIndex, uint invokeCountX, uint invokeCountY =0, uint invokeCountZ = 0)
        {
            if (localUniformAllocation)
            {
                var descriptorSetCount = DescriptorSetCount;
                VkDescriptorBufferBindingInfoEXT* bindingInfo = stackalloc VkDescriptorBufferBindingInfoEXT[descriptorSetCount];
                ulong* offsets = stackalloc ulong[descriptorSetCount];
                uint* indices = stackalloc uint[descriptorSetCount];
                GPUBufferExtensions.WriteFromHostDelayed(_tempUniformBuffer, 0, _computePipeline.UniformBufferSize);
                WriteUniformToDescriptorBuffers();
                for (uint i = 0; i < descriptorSetCount; i++)
                {
                    _tempDescriptorSetInfos[i].WriteFromBuffers(frameIndex);
                    var buffer = _tempDescriptorSetInfos[i].DescriptorBuffers[frameIndex];
                    bindingInfo[i] = buffer.BindingInfo;
                    offsets[i] = 0;
                    indices[i] = i;
                }

                _computePipeline.Dispatch(commandBuffer, VariantIndex, bindingInfo, offsets, indices, invokeCountX, invokeCountY, invokeCountZ);
            }
            else
            {
                _computePipeline.Dispatch(commandBuffer,frameIndex,VariantIndex,invokeCountX, invokeCountY, invokeCountZ);
            }
        }

        public void Reinitialise(Dictionary<int, Vector4UInt> textureRemap, Dictionary<int, Vector4UInt> storageRemap)
        {
            var existingImages = _textures;
            var existingRegions = _storageBuffers;

            _textures = new ITextureProvider[DescriptorSetCount][];
            _storageBuffers = new GPUBuffer[DescriptorSetCount][][];
            for (uint i = 0; i < TotalSets; i++)
            {
                var setInfo = DescriptorSetInfos[i];
                if (setInfo.StorageBufferCount > 0)// && !setInfo.NoAllocStorageBuffers)
                {
                    _storageBuffers[i] = new GPUBuffer[setInfo.StorageBufferCount][];

                    for (uint j = 0; j < setInfo.StorageBufferCount; j++)
                    {
                        _storageBuffers[i][j] = new GPUBuffer[SwapChain.MAX_CONCURRENT_FRAMES];
                    }
                }
                if (setInfo.ImageCount > 0)
                {
                    _textures[i] = new ITextureProvider[setInfo.ImageCount];

                    for (int j = 0; j < setInfo.BindingCount; j++)
                    {
                        DescriptorBinding binding = setInfo._descriptorBindings[j];
                        if (!binding.Image) continue;

                        if (textureRemap.TryGetValue(binding.Id, out var remapIndices))
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

            for (uint i = 0; i < TotalSets; i++)
            {
                var setInfo = DescriptorSetInfos[i];
                for (int j = 0; j < setInfo.BindingCount; j++)
                {
                    if (storageRemap.TryGetValue(setInfo._descriptorBindings[j].Id, out var remapIndices) && remapIndices.Z != uint.MaxValue && remapIndices.W != uint.MaxValue)
                    {
                        _storageBuffers[remapIndices.Z][remapIndices.W] = existingRegions[remapIndices.X][remapIndices.Y];
                        WriteStorageBuffersToDescriptorBuffer(i, setInfo._descriptorBindings[j].BindPoint);
                    }
                }
            }
        }

        public override unsafe void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            GC.SuppressFinalize(this);
            _tempUniformBuffer?.EnqueueForDisposal();
            _tempUniformBuffer = null;
            _computePipeline.RemoveVariant(this);
            if (localUniformAllocation)
            {
                NativeMemory.AlignedFree(pUniformBuffer);
                localUniformAllocation = false;
            }
            pUniformBuffer = null;

            GC.ReRegisterForFinalize(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UpdateVariant(ComputeVariant variant)
        {
            for (uint setIndex = 0; setIndex < variant.DescriptorSetCount; setIndex++)
            {
                for (int i = 0; i < variant.DescriptorSetInfos[setIndex].BindingCount; i++)
                {
                    var binding = variant.DescriptorSetInfos[setIndex]._descriptorBindings[i];
                    if (binding.Image)
                    {
                        variant.WriteTexturesToDescriptorBuffer(setIndex, binding.BindPoint);
                    }
                }
            }
        }
    }
}
