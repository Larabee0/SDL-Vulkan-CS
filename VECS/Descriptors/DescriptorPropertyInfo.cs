using System;
using System.Collections.Generic;
using Vortice.SPIRV;
using Vortice.SPIRV.Reflect;
using Vortice.Vulkan;

namespace VECS
{
    public class DescriptorPropertyInfo
    {
        public readonly string Name;
        public readonly string AbsName;
        public readonly int Id;
        public readonly SpvOp Type;
        public readonly VertexAttributeFormat VectorFormat;
        public readonly uint Offset;
        public readonly uint PaddedSize;
        public readonly bool Signed = false;
        public readonly bool Matrix;
        public readonly uint Rows;
        public readonly uint Columns;
        public readonly bool VariableArraySize;
        public readonly bool FixedArray;
        public readonly uint ArrayDimentions;
        public readonly uint[] ArrayDimentionSizes;
        public readonly DescriptorPropertyInfo[] Members;
        public readonly VkImageViewType ImageType;
        // public readonly bool ImageArray;
        public readonly bool Sampler;
        public readonly uint ImageDepth;
        public readonly Dictionary<string, int> MemberMap;
        public readonly Dictionary<int, int> MemberMap2;

        public uint CachedMemberSize = 0;
        public uint CachedArraySize = 0;

        public uint Size
        {
            get
            {
                switch (Type)
                {
                    case SpvOp.TypeFloat: return VectorFormat.GetAttributeByteSize();
                    case SpvOp.TypeInt: return VectorFormat.GetAttributeByteSize();
                    case SpvOp.TypeVector: return VectorFormat.GetAttributeByteSize();
                    case SpvOp.TypeMatrix: return VectorFormat.GetAttributeByteSize() * Rows;
                    case SpvOp.TypeStruct:

                        if (CachedMemberSize == 0)
                        {
                            for (int i = 0; i < Members.Length; i++)
                            {
                                CachedMemberSize += Members[i].Size;
                            }
                        }
                        return CachedMemberSize;
                    case SpvOp.TypeArray:
                        if (CachedMemberSize == 0)
                        {
                            for (int i = 0; i < Members.Length; i++)
                            {
                                CachedMemberSize += Members[i].Size;
                            }
                        }
                        if (CachedArraySize == 0)
                        {
                            CachedArraySize = ArrayDimentionSizes[0] * CachedMemberSize;
                            for (int i = 1; i < ArrayDimentions; i++)
                            {
                                CachedArraySize *= ArrayDimentionSizes[i];
                            }
                        }
                        if(CachedArraySize == 0 && FixedArray)
                        {
                            CachedArraySize = PaddedSize;
                        }
                        return CachedArraySize;
                    case SpvOp.TypeRuntimeArray:
                        if (CachedMemberSize == 0)
                        {
                            for (int i = 0; i < Members.Length; i++)
                            {
                                CachedMemberSize += Members[i].Size;
                            }
                        }
                        if (CachedArraySize == 0)
                        {
                            CachedArraySize = ArrayDimentionSizes[0] * CachedMemberSize;
                            for (int i = 1; i < ArrayDimentions; i++)
                            {
                                CachedArraySize *= ArrayDimentionSizes[i];
                            }
                        }
                        return CachedArraySize;
                    default: return 0;
                        //throw new NotSupportedException(string.Format("SpvOpType {0} is not an expected variable type",Type.ToString()));
                }
            }
        }

        public DescriptorPropertyInfo(string parentName, string name, SpvOp type, uint paddedSize, SpvReflectNumericTraits traits, uint offset)
        {
            Name = name;
            AbsName = parentName + name;
            Id = AbsName.GetShaderPropertyId();
            Type = type;
            Offset = offset;
            PaddedSize = paddedSize;
            if (type == SpvOp.TypeVector)
            {
                VectorFormat = (traits.vector.component_count * (traits.scalar.width / 8)).GetAttributeFromByteSize();
            }
            if (type == SpvOp.TypeMatrix)
            {
                Rows = traits.matrix.row_count;
                Columns = traits.matrix.column_count;
                VectorFormat = (traits.vector.component_count * Rows).GetAttributeFromByteSize();
            }
            if (type == SpvOp.TypeInt || type == SpvOp.TypeFloat)
            {
                VectorFormat = (traits.scalar.width / 8).GetAttributeFromByteSize();
                Signed = traits.scalar.signedness == 1;
            }
        }

        public unsafe DescriptorPropertyInfo(string parentName, string name, SpvOp type, uint paddedSize, uint offset, SpvReflectArrayTraits arrayTraits, List<DescriptorPropertyInfo> children)
        {
            Name = name;
            AbsName = parentName + name;
            Id = AbsName.GetShaderPropertyId();
            Type = type;
            Offset = offset;
            PaddedSize = paddedSize;
            Members = [.. children];
            ArrayDimentions = arrayTraits.dims_count;
            ArrayDimentionSizes = new uint[arrayTraits.dims_count];
            FixedArray = true;
            for (uint i = 0; i < arrayTraits.dims_count; i++)
            {
                ArrayDimentionSizes[i] = arrayTraits.dims[i];
            }

            MemberMap = new(Members.Length);
            MemberMap2 = new(Members.Length);

            for (int i = 0; i < Members.Length; i++)
            {
                MemberMap.Add(Members[i].Name, i);
                MemberMap2.Add(Members[i].Id, i);
            }
        }

        public unsafe DescriptorPropertyInfo(string parentName, SpvReflectTypeDescription typeDesc, SpvReflectBlockVariable member)
        {
            Name = member.Name;
            AbsName = parentName + member.Name;
            Id = AbsName.GetShaderPropertyId();
            Type = typeDesc.op;
            Offset = member.offset;
            PaddedSize = member.padded_size;
            Members = [];
            ArrayDimentions = member.array.dims_count;
            ArrayDimentionSizes = new uint[member.array.dims_count];
            FixedArray = true;
            for (uint i = 0; i < member.array.dims_count; i++)
            {
                ArrayDimentionSizes[i] = member.array.dims[i];
            }

            MemberMap = [];
            MemberMap2 = [];

            CachedMemberSize = member.size;
        }

        public unsafe DescriptorPropertyInfo(string parentName, string name, SpvOp type, List<DescriptorPropertyInfo> children, uint paddedSize, uint offset)
        {
            Name = name;
            AbsName = parentName + name;
            Id = AbsName.GetShaderPropertyId();
            Type = type;
            Offset = offset;
            PaddedSize = paddedSize;
            Members = [.. children];
            uint memberSize = 0;
            for (int i = 0; i < Members.Length; i++)
            {
                memberSize += Members[i].PaddedSize;
            }

            if (memberSize > PaddedSize)
            {
                PaddedSize = memberSize;
            }

            ArrayDimentions = 1;
            ArrayDimentionSizes = [1];
            VariableArraySize = true;

            MemberMap = new(Members.Length);
            MemberMap2 = new(Members.Length);

            for (int i = 0; i < Members.Length; i++)
            {
                MemberMap.Add(Members[i].Name, i);
                MemberMap2.Add(Members[i].Id, i);
            }
        }

        public DescriptorPropertyInfo(string parentName, string name, SpvOp type, uint paddedSize, uint offset, List<DescriptorPropertyInfo> members)
        {
            Name = name;
            AbsName = parentName + name;
            Id = AbsName.GetShaderPropertyId();
            Type = type;
            Offset = offset;
            PaddedSize = paddedSize;
            Members = [.. members];

            MemberMap = new(Members.Length);
            MemberMap2 = new(Members.Length);

            for (int i = 0; i < Members.Length; i++)
            {
                MemberMap.Add(Members[i].Name, i);
                MemberMap2.Add(Members[i].Id, i);
            }
        }

        public DescriptorPropertyInfo(string parentName, string name, SpvOp type, uint offset, SpvReflectImageTraits imageTraits)
        {
            Name = name;
            AbsName = parentName + name;
            Id = AbsName.GetShaderPropertyId();
            Type = type;
            Offset = offset;
            ImageType = imageTraits.dim switch
            {
                SpvDim.Dim1D => VkImageViewType.Image1D,
                SpvDim.Dim2D => VkImageViewType.Image2D,
                SpvDim.Dim3D => VkImageViewType.Image3D,
                SpvDim.Cube => VkImageViewType.ImageCube,
                _ => throw new NotImplementedException(string.Format("Image dimention {0} is not implemented for descriptor variables", imageTraits.dim.ToString())),
            };

            if (imageTraits.arrayed != 0)
            {
                //ImageArray = true;
                switch (ImageType)
                {
                    case VkImageViewType.Image1D:
                        ImageType = VkImageViewType.Image1DArray;
                        break;
                    case VkImageViewType.Image2D:
                        ImageType = VkImageViewType.Image2DArray;
                        break;
                    case VkImageViewType.Image3D:
                        throw new InvalidOperationException("Cannot have arrayed 3d image!");
                    case VkImageViewType.ImageCube:
                        ImageType = VkImageViewType.ImageCubeArray;
                        break;
                    case VkImageViewType.Image1DArray:
                        break;
                    case VkImageViewType.Image2DArray:
                        break;
                    case VkImageViewType.ImageCubeArray:
                        break;
                    default:
                        throw new NotImplementedException(string.Format("{0} arraying not currently handled!", ImageType.ToString()));
                }
            }

            //if (imageTraits.depth != 0)
            //{
            //    throw new NotImplementedException(string.Format("Image Depth = {0} unhandled", imageTraits.depth));
            //}

            if (imageTraits.ms != 0)
            {
                throw new NotImplementedException(string.Format("Image ms = {0} unhandled", imageTraits.ms));
            }
        }

        public DescriptorPropertyInfo(string parentName, string name, SpvOp type, uint offset)
        {
            Name = name;
            AbsName = parentName + name;
            Id = AbsName.GetShaderPropertyId();
            Type = type;
            Offset = offset;

            Sampler = true;
        }

        public bool LookUpMember(string name, out DescriptorPropertyInfo propertyInfo)
        {
            if (Name == name)
            {
                propertyInfo = this;
                return true;
            }
            if (Members == null || Members.Length == 0)
            {
                propertyInfo = null;
                return false;
            }
            var dotIndex = name.IndexOf('.');
            if (dotIndex >= 0 && MemberMap.TryGetValue(name[..dotIndex], out var memberIndex))
            {
                return Members[memberIndex].LookUpMember(name, out propertyInfo);
            }
            else if (MemberMap.TryGetValue(name, out memberIndex))
            {
                propertyInfo = Members[memberIndex];
                return true;
            }

            propertyInfo = null;
            return false;
        }

        public bool LookUpMember(int id, out DescriptorPropertyInfo propertyInfo)
        {
            if (Id == id)
            {
                propertyInfo = this;
                return true;
            }
            if (Members == null || Members.Length == 0)
            {
                propertyInfo = null;
                return false;
            }
            if (MemberMap2.TryGetValue(id, out var memberIndex))
            {
                propertyInfo = Members[memberIndex];
                return true;
            }
            else
            {
                for (int i = 0; i < Members.Length; i++)
                {
                    if (Members[i].LookUpMember(id, out propertyInfo))
                    {
                        return true;
                    }
                }
            }

            propertyInfo = null;
            return false;
        }
    }
}
