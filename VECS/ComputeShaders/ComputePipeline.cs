using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VECS.LowLevel;
using Vortice.Vulkan;

namespace VECS
{
    public class ComputePipeline : Pipeline
    {
        private readonly ConcurrentQueue<ComputeVariant> _variantsToAdd = new();
        internal ComputeVariant[] _computeVariants;
        public override int VariantCount => _computeVariants.Length;

        private Vector3UInt _localSize = new(1, 1, 1);
        public Vector3UInt LocalSize => _localSize;

        private readonly static ConcurrentDictionary<int, int> _lastBoundComputePipeline = new(Environment.ProcessorCount, Environment.ProcessorCount * 2);

        public unsafe ComputePipeline(string assetName, string shaderName)
        {
            AssetName = assetName;
            var shaderModule = AssetDataBase<ShaderModule>.GetNamed(shaderName);
            _localSize = shaderModule.ComputeShaderLocalSize;
            _shaderHashes = [shaderModule.Hash];
#if DEBUG
            _shaders = [shaderModule];
#endif

            var spirShader = shaderModule.SpvShaderModule;
            var descriptorSetBindings = GPUPipelineUtil.GenerateSharedDescriptorBindings(spirShader);
            _descriptorSetCount = GPUPipelineUtil.GetSetCount(descriptorSetBindings);
            InitialiseDescriptorSets(descriptorSetBindings, 1, int.MinValue, true);
            _pushConstantsHandler = new(spirShader);
            _pipelineLayout = GPUPipelineUtil.CreatePipelineLayout(_descriptorSetLayouts, _pushConstantsHandler, shaderModule);

            VkComputePipelineCreateInfo computePipelineInfo = new()
            {
                layout = _pipelineLayout,
                stage = shaderModule.ShaderStageCreateInfo,
                flags = VkPipelineCreateFlags.DescriptorBufferEXT
            };

            _pipeline = GPUPipelineUtil.CreateComputePipeline(computePipelineInfo);
            GraphicsDevice.SetObjectName(VkObjectType.Pipeline, _pipeline.Handle, AssetName + "_v" + _version);
            _computeVariants = [new ComputeVariant("Default", this, false)];
            _variantsToAdd.TryDequeue(out var variant);

            if (_uniformBufferSize > 0)
            {
                NativeMemory.AlignedFree(variant.pUniformBuffer);
                variant.pUniformBuffer = _uniformBuffer.Buffer.HostPtr;
                variant.localUniformAllocation = false;
            }
            shaderModule.RegisterComputePipeline(this);
        }

        internal DescriptorSetInfo[] GetTemporaryDescriptorSetInfos()
        {
            DescriptorSetInfo[] result = new DescriptorSetInfo[_descriptorSetCount];

            for (uint setIndex = 0; setIndex < _descriptorSetCount; setIndex++)
            {
                result[setIndex] = new DescriptorSetInfo(_descriptorSetLayouts[setIndex], _descriptorSetInfos[setIndex]._descriptorBindings, true, _descriptorSetInfos[setIndex].UnifromBufferOffset, 1);
            }
            return result;
        }

        public void RemoveVariant(ComputeVariant variant)
        {
            _freeVariantIndices.Enqueue(variant.VariantIndex);
            _computeVariants[variant.VariantIndex] = null;
        }

        public void AddVariant(ComputeVariant variant)
        {
            _variantsToAdd.Enqueue(variant);
        }

        public ComputeVariant GetOrCreateVariant(uint index, bool allowTmpAllocation = true)
        {
            if (index < _computeVariants.Length && _computeVariants[index] != null)
            {
                return _computeVariants[index];
            }
            return Create(string.Format("VARAINT_{0}", index), allowTmpAllocation);
        }

        public ComputeVariant Create(string name, bool allowTmpAllocation = false)
        {
            return new ComputeVariant(name, this, true, allowTmpAllocation);
        }

        public ComputeVariant Default()
        {
            return _computeVariants[0];
        }

        protected override unsafe bool AllocNewVariants()
        {
            if (!_variantsToAdd.IsEmpty)
            {
                Array.Resize(ref _computeVariants, (int)_variantCount);
                bool reassignUniformPtrs = false;
                for (int i = 0; i < _descriptorSetCount; i++)
                {
                    _descriptorSetInfos[i].SetVariantLength((uint)VariantCount);
                }
                if (_uniformBufferSize > 0)
                {
                    _uniformBuffer.UpdateUniformCount((uint)VariantCount);
                    _uniformBuffer.SetDebugName(string.Format("{0}_UniformBuffer", AssetName));

                    reassignUniformPtrs = true;
                }
                while (_variantsToAdd.TryDequeue(out var variant))
                {
                    Debug.Assert(_computeVariants[variant.VariantIndex] == null, "Attempting to replace active material!");
                    _computeVariants[variant.VariantIndex] = variant;
                    if (_uniformBufferSize > 0 && variant.localUniformAllocation)
                    {
                        void* pipelineAlloc = _uniformBuffer.UniformAddresses[variant.VariantIndex];
                        if (variant._allowTmpBufferAllocation)
                        {
                            void* localAllocation = variant.pUniformBuffer;
                            Buffer.MemoryCopy(localAllocation, pipelineAlloc, _uniformBufferSize, _uniformBufferSize);
                        }
                        variant.pUniformBuffer = pipelineAlloc;
                    }

                    variant.DiposeTemporaryBuffers();
                    variant.localUniformAllocation = false;
                }

                if (reassignUniformPtrs)
                {
                    for (int i = 0; i < VariantCount; i++)
                    {
                        if (_computeVariants[i] == null) continue;
                        _computeVariants[i].pUniformBuffer = _uniformBuffer.UniformAddresses[i];
                    }
                }
                return true;
            }
            return false;
        }

        public unsafe void Dispatch(VkCommandBuffer commandBuffer, int frameIndex, uint variantIndex, uint invokeCountX, uint invokeCountY = 0, uint invokeCountZ = 0)
        {
            VkDescriptorBufferBindingInfoEXT* bindingInfo = stackalloc VkDescriptorBufferBindingInfoEXT[_descriptorSetCount];
            ulong* offsets = stackalloc ulong[_descriptorSetCount];
            uint* indices = stackalloc uint[_descriptorSetCount];

            for (uint i = 0; i < _descriptorSetCount; i++)
            {
                var buffer = _descriptorSetInfos[i].DescriptorBuffers[frameIndex];
                bindingInfo[i] = buffer.BindingInfo;
                offsets[i] = buffer.AlignedSize * variantIndex;
                indices[i] = i;
            }

            Dispatch(commandBuffer, variantIndex, bindingInfo, offsets, indices, invokeCountX, invokeCountY, invokeCountZ);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Dispatch(VkCommandBuffer commandBuffer, uint pushConstantIndex, VkDescriptorBufferBindingInfoEXT* bindingInfo, ulong* offsets, uint* indices, uint invokeCountX, uint invokeCountY = 0, uint invokeCountZ = 0)
        {
            var threadID = Environment.CurrentManagedThreadId;
            bool init = _lastBoundComputePipeline.TryGetValue(threadID, out var shaderHash);
            
            if(!init || shaderHash != Hash|| shaderHash == int.MaxValue)
            {
                GraphicsDevice.DeviceAPI.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, _pipeline);
                _lastBoundComputePipeline.AddOrUpdate(threadID, Hash,(a, b) => Hash);
            }

            DescriptorBuffer.BindSets(commandBuffer, (uint)_descriptorSetCount, bindingInfo);
            DescriptorBuffer.SetOffsets(commandBuffer, _pipelineLayout, VkPipelineBindPoint.Compute, 0, (uint)_descriptorSetCount, offsets, indices);

            _pushConstantsHandler.BindPushConstants(commandBuffer, _pipelineLayout, pushConstantIndex);
            GraphicsDevice.DeviceAPI.vkCmdDispatch(commandBuffer, invokeCountX.GetComputeShaderGroupCount(_localSize.X), invokeCountY.GetComputeShaderGroupCount(_localSize.Y), invokeCountZ.GetComputeShaderGroupCount(_localSize.Z));
        }

        public static ComputePipeline GetOrCreate(string shaderName)
        {
            var shader = AssetDataBase<ComputePipeline>.GetNamedSilentFail(shaderName);

            if (shader == null)
            {
                shader = new ComputePipeline(shaderName, shaderName);
                AssetDataBase<ComputePipeline>.Add(shader);
            }

            return shader;
        }

        public static Vector2UInt CompensateForWorkGroupLimits(uint totalInvocations)
        {
            var workGroupY = (uint)(int)MathF.Ceiling((float)totalInvocations / (float)GraphicsDevice.MaxWorkGroupX);
            var workGroupX = (uint)Math.Min(totalInvocations, GraphicsDevice.MaxWorkGroupX);

            return new(workGroupX,workGroupY);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void UpdateComputeShaders()
        {
            foreach (var item in _lastBoundComputePipeline)
            {
                _lastBoundComputePipeline[item.Key] = int.MaxValue;
            }
            var count = AssetDataBase<ComputePipeline>.AssetCount;
            var readingList = AssetDataBase<ComputePipeline>.AllAssetsListForReading;
            readingList.ForEach(m => Update(m));
            _descriptorReWrite = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private SwapChainBuffer GetBuffer(DescriptorBinding descriptorBinding)
        {
            return GetBuffer(descriptorBinding.DescriptorSetIndex, descriptorBinding.BindPoint);
        }
        internal static void Update(ComputePipeline pipeline)
        {
            if (pipeline.VariantCount == 0) return;

            for (uint i = 0; i < pipeline.DescriptorSetCount; i++)
            {
                var bindings = pipeline.GetDescriptorBindings(i);
                for (uint j = 0; j < bindings.Length; j++)
                {
                    var binding = bindings[j];
                    if (binding.StorageBuffer && (pipeline.GetBuffer(binding) == null || pipeline.GetBuffer(binding).IsDisposed))
                    {
                        pipeline._descriptorSetInfos[i].SetStorageBuffer(EngineBuffers.TryGetBuffer(binding.Id), binding.BindPoint);
                    }
                    if ((_descriptorReWrite || Presenter.NewSwapChain) && binding.Image)
                    {
                        var texture = EngineTextures.TryGetTexture(binding.Id);
                        if (texture == null) continue;
                        for (int k = 0; k < pipeline.VariantCount; k++)
                        {
                            var variant = pipeline._computeVariants[k];
                            if (variant == null) continue;
                            variant.SetTextures(binding.DescriptorSetIndex, binding.BindPoint, texture);
                        }
                    }
                }
            }

            bool forceDescriptorWrite = pipeline.AllocNewVariants();
            forceDescriptorWrite |= Presenter.NewSwapChain;
            forceDescriptorWrite |= _descriptorReWrite;
            if (forceDescriptorWrite)
            {
                for (int i = 0; i < pipeline.VariantCount; i++)
                {
                    var variant = pipeline._computeVariants[i];
                    if (variant == null) continue;
                    ComputeVariant.UpdateVariant(variant);
                    pipeline.WriteUniformToDescriptorBuffers(variant);
                }
            }

            for (int i = 0; i < pipeline._descriptorSetInfos.Length; i++)
            {
                pipeline._descriptorSetInfos[i].WriteFromBuffers(Presenter.FrameIndex);
            }

            pipeline._uniformBuffer?.WriteToGPU(Presenter.FrameIndex);
        }

        public override VkPipeline ReplacePipeline(VkPipeline pipeline)
        {
            var old = _pipeline;

            _pipeline = pipeline;

            return old;
        }

        public override VkPipeline Recreate()
        {
            ShaderModule shaderModule = AssetDataBase<ShaderModule>.GetHashed(_shaderHashes[0]);
            _localSize = shaderModule.ComputeShaderLocalSize;
            VkComputePipelineCreateInfo computePipelineInfo = new()
            {
                layout = _pipelineLayout,
                stage = shaderModule.ShaderStageCreateInfo,
                flags = VkPipelineCreateFlags.DescriptorBufferEXT
            };

            return GPUPipelineUtil.CreateComputePipeline(computePipelineInfo);
        }

        public override unsafe void Reinitialise()
        {
            _descriptorReWrite = true;
            uint usedVariantCount = (uint)VariantCount;

            ShaderModule shaderModule = AssetDataBase<ShaderModule>.GetHashed(_shaderHashes[0]);
            _localSize = shaderModule.ComputeShaderLocalSize;
            UniformBuffer existingUniformBuffer = _uniformBuffer;
            var oldShaderProperties = new Dictionary<int, ShaderProperty>(_cachedShaderProperties);
            var existingDescriptorSets = _descriptorSetInfos;

            var descriptorSetBindings = GPUPipelineUtil.GetSharedBindings(shaderModule);

            PipelineRecreation.EnqueueForDisposal(_pipeline, _descriptorSetLayouts);

            InitialiseDescriptorSets(descriptorSetBindings, usedVariantCount, int.MinValue, true);

            ClearCachedData();
            // descriptor set data matching
            byte[] bytes;
            Dictionary<int, Vector4UInt> textureRemap = [];
            Dictionary<int, Vector4UInt> storageRemap = [];

            for (int i = 0; i < existingDescriptorSets.Length; i++)
            {
                for (int j = 0; j < existingDescriptorSets[i].BindingCount; j++)
                {
                    var binding = existingDescriptorSets[i]._descriptorBindings[j];

                    if (binding.StorageBuffer)
                    {
                        storageRemap.Add(binding.Id, new(binding.DescriptorSetIndex, (uint)existingDescriptorSets[i].BindPointToBufferIndex[binding.BindPoint], uint.MaxValue, uint.MaxValue));
                    }
                    if (binding.Image)
                    {
                        textureRemap.Add(binding.Id, new(binding.DescriptorSetIndex, (uint)existingDescriptorSets[i].BindPointToImageIndex[binding.BindPoint], uint.MaxValue, uint.MaxValue));
                    }
                }
            }

            for (int i = 0; i < DescriptorSetCount; i++)
            {
                for (int j = 0; j < DescriptorSetInfos[i].BindingCount; j++)
                {
                    var binding = DescriptorSetInfos[i]._descriptorBindings[j];

                    if (binding.StorageBuffer && storageRemap.TryGetValue(binding.Id, out var remap))
                    {
                        remap.Z = binding.DescriptorSetIndex;
                        remap.W = (uint)existingDescriptorSets[i].BindPointToBufferIndex[binding.BindPoint];
                        storageRemap[binding.Id] = remap;
                    }

                    if (binding.Image && textureRemap.TryGetValue(binding.Id, out remap))
                    {
                        remap.Z = binding.DescriptorSetIndex;
                        remap.W = (uint)existingDescriptorSets[i].BindPointToImageIndex[binding.BindPoint];
                        textureRemap[binding.Id] = remap;
                    }
                }
            }

            // remapping for textures and storage buffer regions doesnt work bc lookuprpoperty will return false for global properties
            // it needs complete remap even for global properties

            foreach (var oldProperty in oldShaderProperties)
            {
                if (LookUpProperty(oldProperty.Key, out var newProperty))
                {
                    var oldShaderProperty = oldProperty.Value;

                    if (newProperty.BindingInfo.StorageBuffer && newProperty.BindingInfo.BufferSize == oldShaderProperty.BindingInfo.BufferSize)
                    {
                        var oldSet = existingDescriptorSets[oldShaderProperty.SetIndex];
                        var newSet = _descriptorSetInfos[newProperty.SetIndex];

                        var oldBuffer = oldSet.GetBuffer(oldShaderProperty.BindPoint);
                        var newBuffer = newSet.GetBuffer(newProperty.BindPoint);

                        var index = oldSet.BindPointToBufferIndex[oldShaderProperty.BindPoint];

                        Buffer.MemoryCopy(oldBuffer.HostPtr, newBuffer.HostPtr, newBuffer.HostBufferSize, Math.Min(oldBuffer.HostBufferSize, newBuffer.HostBufferSize));
                    }

                    if (existingUniformBuffer == null || _uniformBuffer == null) continue;

                    if (newProperty.Property != null && oldShaderProperty.Property != null && newProperty.Property.Size == oldShaderProperty.Property.Size)
                    {
                        bytes = new byte[newProperty.Property.Size];

                        for (uint i = 0; i < VariantCount; i++)
                        {
                            existingUniformBuffer.ReadFromUniformBuffer(i, oldShaderProperty, ref bytes);
                            _uniformBuffer.WriteToUniformBuffer(i, newProperty, bytes);
                        }
                    }
                }
            }

            for (int i = 0; i < _computeVariants.Length; i++)
            {
                _computeVariants[i]?.Reinitialise(textureRemap, storageRemap);
                if (_uniformBuffer != null)
                {
                    _computeVariants[i].pUniformBuffer = _uniformBuffer.UniformAddresses[i];
                }
            }

            _pushConstantsHandler = new(shaderModule);

            VkComputePipelineCreateInfo computePipelineInfo = new()
            {
                layout = _pipelineLayout,
                stage = shaderModule.ShaderStageCreateInfo,
                flags = VkPipelineCreateFlags.DescriptorBufferEXT
            };

            _pipelineLayout = GPUPipelineUtil.CreatePipelineLayout(_descriptorSetLayouts, _pushConstantsHandler, shaderModule);
            _pipeline = GPUPipelineUtil.CreateComputePipeline(computePipelineInfo);

            GraphicsDevice.SetObjectName(VkObjectType.Pipeline, _pipeline.Handle, AssetName + "_v" + _version);
            for (int i = 0; i < existingDescriptorSets.Length; i++)
            {
                existingDescriptorSets[i].Dispose();
            }
            existingUniformBuffer?.Dispose();
        }
    }
}
