using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.SPIRV;
using Vortice.SPIRV.Reflect;

namespace VECS
{
    public static class SPIRVReflectUtil
    {
        public static unsafe SpvReflectShaderModule CreateReflectShaderModule(byte[] spirv_code)
        {
            SpvReflectShaderModule module;
            byte[] shaderBytes = spirv_code;
            SpvReflectResult result = SPIRVReflectApi.spvReflectCreateShaderModule(shaderBytes, &module);
            if (result != SpvReflectResult.Success)
            {
                throw new Exception(string.Format("Failed to create reflected shader module: {0}", result.ToString()));
            }
            return module;
        }

        public static unsafe void DestroyReflectShaderModule(SpvReflectShaderModule module)
        {
            SPIRVReflectApi.spvReflectDestroyShaderModule(&module);
        }

        public unsafe static SpvReflectInterfaceVariable[] EnumerateInputVariables(SpvReflectShaderModule module)
        {
            uint var_count = 0;

            var result = SPIRVReflectApi.spvReflectEnumerateInputVariables(&module, &var_count, null);
            if (result != SpvReflectResult.Success)
            {
                throw new Exception(string.Format("Failed to enumerate input variables: {0}", result.ToString()));
            }
            if (var_count == 0) return null;

            SpvReflectInterfaceVariable[] variables = new SpvReflectInterfaceVariable[var_count];
            SpvReflectInterfaceVariable** input_vars = (SpvReflectInterfaceVariable**)NativeMemory.AllocZeroed((nuint)(var_count * sizeof(SpvReflectInterfaceVariable*)));

            result = SPIRVReflectApi.spvReflectEnumerateInputVariables(&module, &var_count, input_vars);

            if (result != SpvReflectResult.Success)
            {
                throw new Exception(string.Format("Failed to enumerate input variables: {0}", result.ToString()));
            }

            for (int i = 0; i < var_count; i++)
            {
                variables[i] = *input_vars[i];
            }

            NativeMemory.Free(input_vars);
            input_vars = null;

            return variables;
        }

        public unsafe static SpvReflectBlockVariable[] PushConstants(SpvReflectShaderModule module)
        {
            uint block_count = 0;
            var result = SPIRVReflectApi.spvReflectEnumeratePushConstantBlocks(&module, &block_count, null);
            if (result != SpvReflectResult.Success)
            {
                throw new Exception(string.Format("Failed to enumerate push constant blocks: {0}", result.ToString()));
            }
            if (block_count == 0) return null;
            
            SpvReflectBlockVariable[] pushConstants = new SpvReflectBlockVariable[block_count];

            SpvReflectBlockVariable** pPushConstants = (SpvReflectBlockVariable**)NativeMemory.Alloc((nuint)(block_count * sizeof(SpvReflectBlockVariable)));
            
            result = SPIRVReflectApi.spvReflectEnumeratePushConstantBlocks(&module, &block_count, pPushConstants); if (result != SpvReflectResult.Success)
            {
                throw new Exception(string.Format("Failed to enumerate push constant blocks: {0}", result.ToString()));
            }

            for (int i = 0; i < block_count; i++)
            {
                pushConstants[i] = *pPushConstants[i];
            }

            NativeMemory.Free(pPushConstants);
            pPushConstants = null;

            return pushConstants;
        }

        public unsafe static SpvReflectDescriptorBinding[] DescriptorBindings(SpvReflectShaderModule module)
        {
            uint binding_count = 0;
            var result = SPIRVReflectApi.spvReflectEnumerateDescriptorBindings(&module, &binding_count, null);
            if (result != SpvReflectResult.Success)
            {
                throw new Exception(string.Format("Failed to enumerate descriptor bindings: {0}", result.ToString()));
            }
            if (binding_count == 0) return null;
            
            SpvReflectDescriptorBinding[] bindings = new SpvReflectDescriptorBinding[binding_count];

            SpvReflectDescriptorBinding** pDescriptorBindings = (SpvReflectDescriptorBinding**)NativeMemory.AllocZeroed((nuint)(binding_count * sizeof(SpvReflectBlockVariable*)));
            
            result = SPIRVReflectApi.spvReflectEnumerateDescriptorBindings(&module, &binding_count, pDescriptorBindings);
            
            if (result != SpvReflectResult.Success)
            {
                throw new Exception(string.Format("Failed to enumerate descriptor bindings: {0}", result.ToString()));
            }

            for (int i = 0; i < binding_count; i++)
            {
                bindings[i] = *pDescriptorBindings[i];
            }

            NativeMemory.Free(pDescriptorBindings);
            pDescriptorBindings = null;

            return bindings;
        }

        public static DescriptorPropertyInfo[] GetBindingMembers(SpvReflectDescriptorBinding binding, string bindingParentName)
        {
            return binding.descriptor_type switch
            {
                SpvReflectDescriptorType.Sampler => [GetBlockSampler(bindingParentName, binding)],
                SpvReflectDescriptorType.CombinedImageSampler => [GetBlockImage(bindingParentName, binding, binding.image)],
                SpvReflectDescriptorType.SampledImage => [GetBlockImage(bindingParentName, binding, binding.image)],
                SpvReflectDescriptorType.StorageImage => [GetBlockImage(bindingParentName, binding, binding.image)],
                SpvReflectDescriptorType.UniformBuffer => [.. GetBlockMembers(bindingParentName, binding.block)],
                SpvReflectDescriptorType.StorageBuffer => [.. GetBlockMembers(bindingParentName, binding.block)],
                SpvReflectDescriptorType.UniformBufferDynamic => [.. GetBlockMembers(bindingParentName, binding.block)],
                SpvReflectDescriptorType.StorageBufferDynamic => [.. GetBlockMembers(bindingParentName, binding.block)],
                _ => throw new NotImplementedException(string.Format("Descriptor type not implemented {0}", binding.descriptor_type.ToString())),
            };
        }

        public unsafe static List<DescriptorPropertyInfo> GetBlockMembers(string bindingParentName, SpvReflectBlockVariable variable)
        {
            var memberCount = variable.member_count;
            var members = variable.members;
            List<DescriptorPropertyInfo> properties = [];

            static bool HasFlag(SpvReflectTypeFlags flags,SpvReflectTypeFlags type)
            {
                return (flags & type) == type;
            }

            if (memberCount == 0 && variable.type_description->op == SpvOp.TypeRuntimeArray)
            {
                SpvReflectTypeFlags flags = variable.type_description->type_flags;
                var traits = variable.type_description->traits;
                if (HasFlag(flags, SpvReflectTypeFlags.FlagMatrix))
                {
                    properties.Add(new(bindingParentName,variable.Name, SpvOp.TypeMatrix, traits.array.stride, traits.numeric, 0));
                }
                else if (HasFlag(flags, SpvReflectTypeFlags.FlagVector))
                {
                    properties.Add(new(bindingParentName,variable.Name, SpvOp.TypeVector, traits.array.stride, traits.numeric, 0));
                }
                else if (HasFlag(flags, SpvReflectTypeFlags.FlagFloat))
                {
                    properties.Add(new(bindingParentName,variable.Name, SpvOp.TypeFloat, traits.array.stride, traits.numeric, 0));
                }
                else if (HasFlag(flags, SpvReflectTypeFlags.FlagInt))
                {
                    properties.Add(new(bindingParentName,variable.Name, SpvOp.TypeInt, traits.array.stride, traits.numeric, 0));
                }
                else if (HasFlag(flags, SpvReflectTypeFlags.FlagBool))
                {
                    Console.WriteLine("New Type bool \"{1}\" Size {0}", variable.size, variable.Name);
                    throw new NotImplementedException("Bool type not implemented for descriptor sets");
                }
            }
            bindingParentName += ".";
            for (uint i = 0; i < memberCount; i++)
            {
                var member = members[i];
                var propertyAbsName = bindingParentName;
                var type_desc = *member.type_description;
                switch (type_desc.op)
                {
                    case SpvOp.TypeSampler:
                        Console.WriteLine("New Type Sampler \"{1}\" Size {0}", member.size, member.Name);
                        throw new NotImplementedException("TypeSampler type not implemented for descriptor sets");
                    case SpvOp.TypeSampledImage:
                        Console.WriteLine("New Type SampledImage \"{1}\" Size {0}", member.size, member.Name);
                        throw new NotImplementedException("SampledImage type not implemented for descriptor sets");
                    case SpvOp.TypeStruct:
                        var structMembers = GetBlockMembers(propertyAbsName+member.Name, member);
                        properties.Add(new(propertyAbsName,member.Name, type_desc.op, member.padded_size, member.offset, structMembers));
                        break;
                    case SpvOp.TypeArray:
                        var arrayChildren = GetBlockMembers(propertyAbsName+member.Name, member);
                        if (arrayChildren.Count == 0)
                        {
                            properties.Add(new(propertyAbsName, type_desc, member));
                        }
                        else
                        {
                            properties.Add(new(propertyAbsName, member.Name, type_desc.op, member.padded_size, member.offset, member.array, arrayChildren));
                        }
                        break;
                    case SpvOp.TypeRuntimeArray:
                        arrayChildren = GetBlockMembers(propertyAbsName+member.Name, member);
                        properties.Add(new(propertyAbsName,member.Name, type_desc.op, arrayChildren, member.padded_size, member.offset));
                        break;
                    case SpvOp.TypeVector:
                        properties.Add(new(propertyAbsName,member.Name, type_desc.op, member.padded_size, member.numeric, member.offset));
                        break;
                    case SpvOp.TypeMatrix:
                        properties.Add(new(propertyAbsName,member.Name, type_desc.op, member.padded_size, member.numeric, member.offset));
                        break;
                    case SpvOp.TypeBool:
                        Console.WriteLine("New Type bool \"{1}\" Size {0}", member.size, member.Name);
                        throw new NotImplementedException("Bool type not implemented for descriptor sets");
                    case SpvOp.TypeFloat:
                        properties.Add(new(propertyAbsName,member.Name, type_desc.op, member.padded_size, member.numeric, member.offset));
                        break;
                    case SpvOp.TypeInt:
                        properties.Add(new(propertyAbsName,member.Name, type_desc.op, member.padded_size, member.numeric, member.offset));
                        break;
                    default:
                        throw new NotImplementedException(string.Format("Un implemented variable block type {0}", type_desc.op.ToString()));
                }
            }
            return properties;
        }

        public static DescriptorPropertyInfo GetBlockImage(string bindingParentName, SpvReflectDescriptorBinding bindings, SpvReflectImageTraits traits)
        {
            if(traits.sampled == 1)
            {
                return new(bindingParentName, bindings.Name, SpvOp.SampledImage, 0, traits);
            }
            else if(traits.sampled == 2)
            {
                return new(bindingParentName, bindings.Name, SpvOp.Image, 0, traits);
            }

                throw new NotImplementedException(string.Format("Image type not implemented for sampled = {0}", traits.sampled.ToString()));
        }

        public static DescriptorPropertyInfo GetBlockSampler(string bindingParentName, SpvReflectDescriptorBinding bindings)
        {
            return new DescriptorPropertyInfo(bindingParentName, bindings.Name, SpvOp.ConstantSampler,0);
        }
    }
}
