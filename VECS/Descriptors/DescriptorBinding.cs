using System;
using System.Text;
using VECS.LowLevel;
using Vortice.SPIRV.Reflect;
using Vortice.Vulkan;

namespace VECS
{
    public sealed class DescriptorBinding
    {
        public readonly string Name;
        public readonly int Id;
        public readonly uint DescriptorSetIndex;
        public readonly uint BindPoint;
        public readonly DescriptorPropertyInfo[] Variables;

        public readonly bool Sampler;
        public readonly bool Image;
        public readonly bool ImageBindingArray;
        public readonly bool Buffer;
        public readonly bool DynamicBuffer;
        public readonly uint BufferSize;
        public readonly uint Stride;
        public readonly VkBufferUsageFlags BufferUsageFlags;
        public readonly bool GlobalUniformBuffer;
        public readonly bool UniformBuffer;
        public readonly VkDescriptorType DescriptorType;
        public readonly VkShaderStageFlags ShaderStage;
        public VkDescriptorSetLayoutBinding VkSetLayoutBinding;

        public bool IsAnyBuffer => Buffer || DynamicBuffer;
        public bool StorageBuffer => !UniformBuffer && IsAnyBuffer;

        public unsafe DescriptorBinding(SpvReflectDescriptorBinding descriptorBinding, VkShaderStageFlags shaderStageFlags)
        {
            Name = descriptorBinding.Name;
            Id = Name.GetShaderPropertyId();
            BindPoint = descriptorBinding.binding;
            DescriptorSetIndex = descriptorBinding.set;
            ShaderStage = shaderStageFlags;
            switch (descriptorBinding.descriptor_type)
            {
                case SpvReflectDescriptorType.CombinedImageSampler:
                    Image = true;
                    ImageBindingArray = descriptorBinding.array.dims_count != 0;
                    break;
                case SpvReflectDescriptorType.UniformBuffer:
                    BufferUsageFlags = VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.UniformBuffer;
                    UniformBuffer = Buffer = true;
                    break;
                case SpvReflectDescriptorType.StorageBuffer:
                    BufferUsageFlags = VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.StorageBuffer;
                    Buffer = true;
                    break;
                case SpvReflectDescriptorType.UniformBufferDynamic:
                    BufferUsageFlags = VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.UniformBuffer;
                    UniformBuffer = DynamicBuffer = true;
                    break;
                case SpvReflectDescriptorType.StorageBufferDynamic:
                    BufferUsageFlags = VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.StorageBuffer;
                    DynamicBuffer = true;
                    break;
                case SpvReflectDescriptorType.StorageImage:
                    Image = true;
                    ImageBindingArray = descriptorBinding.array.dims_count != 0;
                    break;
                case SpvReflectDescriptorType.Sampler:
                    Sampler = true;
                    break;
                case SpvReflectDescriptorType.SampledImage:
                    Image = true;
                    ImageBindingArray = descriptorBinding.array.dims_count != 0;
                    break;
                default:
                    throw new NotImplementedException(string.Format("Descriptor type not implemented {0}", descriptorBinding.descriptor_type.ToString()));
            }

            VkSetLayoutBinding = new()
            {
                binding = descriptorBinding.binding,
                descriptorCount = descriptorBinding.count,
                descriptorType = DescriptorType=(VkDescriptorType)descriptorBinding.descriptor_type,
                stageFlags = shaderStageFlags
            };

            if(Image && ImageBindingArray)
            {
                var imageArrayInfo = descriptorBinding.array;

                var count = this.GetImageBindingArraySize();

                if (descriptorBinding.array.dims[0] <= 1 && count != 0)
                {
                    VkSetLayoutBinding.descriptorCount = Math.Max(1, count);
                }
                else
                {
                    VkSetLayoutBinding.descriptorCount = imageArrayInfo.dims[0];
                }
            }
            

            Variables = [.. SPIRVReflectUtil.GetBindingMembers(descriptorBinding, Name)];

            for (int i = 0; i < Variables.Length; i++)
            {
                BufferSize += Variables[i].PaddedSize;
            }

            if (UniformBuffer)
            {
                BufferSize = (uint)GPUBufferExtensions.GetAlignment(BufferSize, VkBufferUsageFlags.UniformBuffer);
            }
            else if (StorageBuffer)
            {
                BufferSize = (uint)GPUBufferExtensions.GetAlignment(BufferSize,VkBufferUsageFlags.StorageBuffer);
            }

            
            Stride = BufferSize;

            GlobalUniformBuffer = BindPoint == 0 && DescriptorSetIndex == 0 && Name == "ubo";

        }

        public void UpdateShaderStage(VkShaderStageFlags flags)
        {
            VkSetLayoutBinding.stageFlags = flags;
        }

        public DescriptorPropertyInfo GetProperty(int id)
        {
            for (int i = 0; i < Variables.Length; i++)
            {
                if (Variables[i].LookUpMember(id, out var propertyInfo))
                {
                    return propertyInfo;
                }
            }

            return null;
        }

        public override string ToString()
        {
            StringBuilder stringBuilder = new();

            stringBuilder.AppendLine(Name);


            foreach (var item in Variables)
            {
                stringBuilder.AppendLine(item.Name);
            }

            return stringBuilder.ToString();

        }

        public static bool operator ==(DescriptorBinding left, DescriptorBinding right)
        {
            return (left is null && right is null) || (left is not null && right is not null && left.Name == right.Name && left.DescriptorSetIndex == right.DescriptorSetIndex && left.BindPoint == right.BindPoint
                && left.Image == right.Image && left.Buffer == right.Buffer
                && left.DynamicBuffer == right.DynamicBuffer && left.BufferSize == right.BufferSize
                && left.BufferUsageFlags == right.BufferUsageFlags && left.GlobalUniformBuffer == right.GlobalUniformBuffer
                && left.DescriptorType == right.DescriptorType);
        }

        public static bool operator !=(DescriptorBinding left, DescriptorBinding right)
        {
            return !(left == right);
        }

        public override bool Equals(object obj)
        {
            if(obj is not DescriptorBinding binding) return false;
            return this == binding;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Name, DescriptorSetIndex, BindPoint, Image, DescriptorType, VkSetLayoutBinding);
        }

        public DescriptorPropertyInfo GetRunTimeArray()
        {
            if (StorageBuffer && Variables.Length == 1 && Variables[0].Type == Vortice.SPIRV.SpvOp.TypeRuntimeArray)
            {
                return Variables[0];
            }
            return null;
        }

        public DescriptorPropertyInfo GetTexture()
        {
            if (Image && Variables[0].Type == Vortice.SPIRV.SpvOp.SampledImage)
            {
                return Variables[0];
            }
            return null;
        }
    }
}
