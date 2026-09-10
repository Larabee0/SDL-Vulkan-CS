using System;
using System.Runtime.CompilerServices;

namespace VECS
{
    public static class ComputeExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint GetComputeShaderGroupCount(this uint threadCount, uint localSize)
        {
            return Math.Max(1, (threadCount + localSize - 1) / localSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint GetComputeShaderGroupCount(this int threadCount, uint localSize)
        {
            return Math.Max(1, ((uint)threadCount + localSize - 1) / localSize);
        }

    }
}
