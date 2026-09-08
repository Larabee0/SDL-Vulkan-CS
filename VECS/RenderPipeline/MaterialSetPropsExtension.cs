using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace VECS
{
    public static class MaterialSetPropsExtension
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetInt(this Material material, int propertyId, int value)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteToBuffer(propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetUint(this Material material, int propertyId, uint value)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteToBuffer(propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetFloat(this Material material, int propertyId, float value)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteToBuffer(propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetVector2(this Material material, int propertyId, Vector2 value)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteToBuffer(propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetVector4(this Material material, int propertyId, Vector4 value)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteToBuffer(propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetMatrix3x2(this Material material, int propertyId, Matrix3x2 value)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteToBuffer(propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetMatrix3x3(this Material material, int propertyId, Matrix3x3 value)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteToBuffer(propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetMatrix4x4(this Material material, int propertyId, Matrix4x4 value)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteToBuffer(propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetFloatArray(this Material material, int propertyId, Span<float> value)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteArrayToBuffer( propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetVector2Array(this Material material, int propertyId, Span<Vector2> value)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteArrayToBuffer( propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetVector4Array(this Material material, int propertyId, Span<Vector4> value)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteArrayToBuffer(propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetMatrix3x2Array(this Material material, int propertyId, Span<Matrix3x2> value)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteArrayToBuffer(propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetMatrix3x3Array(this Material material, int propertyId, Span<Matrix3x3> value)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteArrayToBuffer(propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetMatrix4x4Array(this Material material, int propertyId, Span<Matrix4x4> value)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteArrayToBuffer(propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetArray<T>(this Material material, int propertyId, Span<T> value) where T : unmanaged
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.WriteArrayToBuffer(propertyInfo, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe void WriteArrayToBuffer<T>(this Material material, ShaderProperty propertyInfo, Span<T> values) where T : unmanaged
        {
            material.Pipeline.WriteArrayToBuffer(material.pUniformBuffer, propertyInfo, values);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe void WriteToBuffer<T>(this Material material, int propertyId, T element) where T : unmanaged
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.Pipeline.WriteToUniformBuffer(material.pUniformBuffer, propertyInfo, element);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe void WriteToBuffer<T>(this Material material, ShaderProperty propertyInfo, T element) where T : unmanaged
        {
            material.Pipeline.WriteToUniformBuffer(material.pUniformBuffer, propertyInfo, element);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool LookUpProperty(this Material material, int propertyId, out ShaderProperty propertyInfo)
        {
            return material.Pipeline.LookUpProperty(propertyId, out propertyInfo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTexture(this Material material, ShaderProperty propertyInfo, Texture texture)
        {
            material.SetTexture(propertyInfo.SetIndex, propertyInfo.BindPoint, texture);
        }

        public static void SetSampler(this Material material, ShaderProperty propertyInfo, TextureSampler sampler)
        {
            material.SetSampler(propertyInfo.SetIndex,propertyInfo.BindPoint, sampler);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTexture(this Material material, int propertyId, ITextureProvider texture)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.SetTexture(propertyInfo.SetIndex, propertyInfo.BindPoint, texture);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTexture(this Material material, ShaderProperty propertyInfo, ITextureProvider texture)
        {
            material.SetTexture(propertyInfo.SetIndex, propertyInfo.BindPoint, texture);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTexture(this Material material, int propertyId, Texture texture)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.SetTexture(propertyInfo, texture);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetSampler(this Material material, int propertyId, TextureSampler sampler)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo) && propertyInfo.BindingInfo.Sampler)
            {
                material.SetSampler(propertyInfo, sampler);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTexture(this Material material, int propertyId,Texture2D texture)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.SetTexture(propertyInfo, texture);
            }
        }

        public static void SetTextures(this Material material, int propertyId, ITextureProvider textures)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.SetTexture(propertyInfo, textures);
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTextureArray(this Material material, int propertyId, Texture2DArray texture)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.SetTexture(propertyInfo, texture);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetCubeMap(this Material material, int propertyId, Cubemap cubemap)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.SetTexture(propertyInfo, cubemap);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetCubeMapArray(this Material material, int propertyId, CubemapArray cubemap)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.SetTexture(propertyInfo, cubemap);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetCubeMap(this Material material, int propertyId, BindingArrayTexture cubemaps)
        {
            if (material.LookUpProperty(propertyId, out var propertyInfo))
            {
                material.SetTexture(propertyInfo, cubemaps);
            }
        }
    }
}
