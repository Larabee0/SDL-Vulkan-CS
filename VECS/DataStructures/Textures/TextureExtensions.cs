using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using VECS.LowLevel;
using Vortice.Vulkan;

namespace VECS
{
    public static class TextureExtensions
    {
        private class TextureBufferCopyCmd
        {
            public readonly Texture Texture;
            public readonly GPUBuffer Buffer;
            public readonly ulong[] Offsets;
            public readonly VkExtent3D[] Extents;
            public readonly bool DisposeBufferAfterCopy;
            public readonly bool DisallowMipMapRegen;
            public readonly bool DirectCopyCmd;
            public readonly VkBufferImageCopy CopyCmd;
            public readonly VkBufferImageCopy[] CopyCmds;

            public TextureBufferCopyCmd(Texture target, GPUBuffer source, bool disposeBufferAfterCopy, bool disallowMipMapRegen)
            {
                Texture = target;
                Buffer = source;
                DisposeBufferAfterCopy = disposeBufferAfterCopy;
                DisallowMipMapRegen = disallowMipMapRegen;
            }

            public TextureBufferCopyCmd(Texture target, GPUBuffer source, bool disposeBufferAfterCopy, ulong[] offsets, VkExtent3D[] extents)
            {
                Texture = target;
                Buffer = source;
                DisposeBufferAfterCopy = disposeBufferAfterCopy;
                DisallowMipMapRegen = true;
                Offsets = offsets;
                Extents = extents;
            }

            public TextureBufferCopyCmd(Texture target, GPUBuffer source, VkBufferImageCopy copyCmd,bool disposeBufferAfterCopy)
            {
                Texture = target;
                Buffer = source;
                DisposeBufferAfterCopy = disposeBufferAfterCopy;
                CopyCmd = copyCmd;
                DirectCopyCmd = true;
            }

            public TextureBufferCopyCmd(Texture target, GPUBuffer source, VkBufferImageCopy[] copyCmds, bool disposeBufferAfterCopy)
            {
                Texture = target;
                Buffer = source;
                DisposeBufferAfterCopy = disposeBufferAfterCopy;
                CopyCmds = copyCmds;
                DirectCopyCmd = true;
            }
        }

        private class SetTextureLayoutCmd
        {
            public readonly Texture Texture;
            public readonly VkImageLayout NewImageLayout;
            public readonly VkPipelineStageFlags2 SrcStage;
            public readonly VkPipelineStageFlags2 DstStage;

            public SetTextureLayoutCmd(Texture texture, VkImageLayout newImageLayout, VkPipelineStageFlags2 srcStage, VkPipelineStageFlags2 dstStage)
            {
                Texture = texture;
                NewImageLayout = newImageLayout;
                SrcStage = srcStage;
                DstStage = dstStage;
            }
        }

        private class DisposeTextureCmd
        {
            public readonly VkImage Image;
            public readonly VmaAllocation Allocation;
            public readonly VkImageView Imageview;
            public readonly VkSampler Sampler;

            public int frameIndex;
            private bool Disposed;

#if DEBUG
            private StackTrace stackTrace;
            private string StackTrack => stackTrace.ToString();
#endif

            public DisposeTextureCmd(VkImage image, VmaAllocation allocation, VkImageView imageview, VkSampler sampler)
            {
                Image = image;
                Allocation = allocation;
                Imageview = imageview;
                Sampler = sampler;
#if DEBUG
                stackTrace = new StackTrace();
#endif
            }

            public static void Dispose(DisposeTextureCmd cmd)
            {
                if (cmd.Disposed) return;
                if (cmd.Sampler != VkSampler.Null)
                {
                    GraphicsDevice.DeviceAPI.vkDestroySampler(cmd.Sampler);
                }
                if (cmd.Imageview != VkImage.Null)
                {
                    GraphicsDevice.DeviceAPI.vkDestroyImageView(cmd.Imageview);
                }
                if (cmd.Image != VkImage.Null && cmd.Allocation != VmaAllocation.Null)
                {
                    Vma.vmaDestroyImage(GraphicsDevice.VmaAllocator, cmd.Image, cmd.Allocation);
                }
                cmd.Disposed = true;
            }
        }

        private readonly static ConcurrentQueue<TextureBufferCopyCmd> _copyBufferToTexture = [];
        private readonly static ConcurrentQueue<TextureBufferCopyCmd> _copyTextureToBuffer = [];
        private readonly static ConcurrentQueue<Texture> _regenMipMapsCmds = [];
        private readonly static ConcurrentQueue<SetTextureLayoutCmd> _setLayoutCmdsQueue = [];

        private readonly static List<SetTextureLayoutCmd> _setLayoutCmds = [];

        private readonly static ConcurrentQueue<DisposeTextureCmd> _disposalQueue = [];
        private readonly static List<DisposeTextureCmd> _disposalList = [];

        private readonly static ConcurrentQueue<Texture> _recreateSamplerQueue = [];

        private readonly static ConcurrentDictionary<int, TextureSampler> _samplers = [];

        private static ConcurrentDictionary<VkFormat, byte[]> _componentBits;

        private static readonly VmaAllocationCreateInfo _allocationCreateInfo = new()
        {
            usage = VmaMemoryUsage.Auto,
            priority = 1
        };

        static TextureExtensions()
        {
            Reset();
        }

        public static void Reset()
        {
            _copyBufferToTexture.Clear();
            _regenMipMapsCmds.Clear();
            var allFormats = Enum.GetValues<VkFormat>();
            _componentBits = new(Environment.ProcessorCount, allFormats.Length);
            foreach (var format in allFormats)
            {
                var componentCount = Vulkan.ComponentCount(format);
                byte[] componentBitsPerPixel = new byte[componentCount];
                for (int i = 0; i < componentCount; i++)
                {
                    componentBitsPerPixel[i] = Vulkan.ComponentBits(format, i);
                }
                _componentBits.TryAdd(format, componentBitsPerPixel);
            }


            while (_disposalQueue.TryDequeue(out var cmd))
            {
                DisposeTextureCmd.Dispose(cmd);
            }
            for (int i = _disposalList.Count - 1; i >= 0; i--)
            {
                DisposeTextureCmd.Dispose(_disposalList[i]);
            }
            _disposalList.Clear();
        }


        public static unsafe void PlayerbackDisposeCmds()
        {
            for (int i = _disposalList.Count - 1; i >= 0; i--)
            {
                if ((long)Presenter.FrameCount > _disposalList[i].frameIndex)
                {
                    DisposeTextureCmd.Dispose(_disposalList[i]);
                    _disposalList.RemoveAt(i);
                }
            }

            if (!_disposalQueue.IsEmpty)
            {
                _disposalList.EnsureCapacity(_disposalQueue.Count);
            }
            while (_disposalQueue.TryDequeue(out var cmd))
            {
                cmd.frameIndex = (int)Presenter.FrameCount + SwapChain.MAX_CONCURRENT_FRAMES;
                _disposalList.Add(cmd);
            }

            while(_recreateSamplerQueue.TryDequeue(out var recreate))
            {
                if(!_samplers.TryGetValue( recreate.GetSamplerId(), out var sampler))
                {
                    _samplers[sampler.SamplerId] = sampler = new(recreate.GetSamplerCreateInfo());
                }
                recreate._textureSampler = sampler;
                recreate.UpdateDescriptor();
            }
        }

        public static void EnqueueForDisposal(VkImage image, VmaAllocation allocation, VkImageView imageView, VkSampler sampler)
        {
            _disposalQueue.Enqueue(new(image, allocation, imageView, sampler));
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] GetBitsPerPixel(VkFormat format)
        {
            return _componentBits[format];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint CalculateMipMapLevels(int w, int h)
        {
            return (uint)Math.Floor(Math.Log2(Math.Max(w, h))) + 1u;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint CalculateMipMapLevels(uint w, uint h)
        {
            return (uint)Math.Floor(Math.Log2(Math.Max(w, h))) + 1u;
        }

        #region Image, View & Sampler Creation
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CreateSampler(this Texture texture)
        {
            texture._textureSampler = GetOrCreateSample(texture.GetSamplerCreateInfo());
        }

        public static TextureSampler GetOrCreateSample(VkSamplerCreateInfo samplerCreateInfo)
        {
            if(!_samplers.TryGetValue(GetSamplerId(samplerCreateInfo), out var sampler))
            {
                sampler = new(samplerCreateInfo);
                _samplers.TryAdd(sampler.SamplerId, sampler);
            }
            return sampler;
        }

        public unsafe static int GetSamplerId(VkSamplerCreateInfo samplerCreateInfo)
        {
            return ShaderProperties.Hash((byte*)&samplerCreateInfo, (uint)sizeof(VkSamplerCreateInfo));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe void CreateSampler(this Texture texture, VkSamplerCreateInfo createInfo)
        {
            if (!_samplers.TryGetValue(GetSamplerId(createInfo), out var sampler))
            {
                sampler = new(createInfo);
                _samplers.TryAdd(sampler.SamplerId, sampler);
            }

            texture._textureSampler = sampler;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static VkImageType GetImageTypeFromViewType(VkImageViewType viewType)
        {
            return viewType switch
            {
                VkImageViewType.Image1D => VkImageType.Image1D,
                VkImageViewType.Image2D => VkImageType.Image2D,
                VkImageViewType.Image3D => VkImageType.Image3D,
                VkImageViewType.ImageCube => VkImageType.Image2D,
                VkImageViewType.Image1DArray => VkImageType.Image2D,
                VkImageViewType.Image2DArray => VkImageType.Image2D,
                VkImageViewType.ImageCubeArray => VkImageType.Image2D,
                _ => throw new ArgumentOutOfRangeException(nameof(viewType)),
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe void CreateImageView(this Texture texture, VkImageViewCreateInfo createInfo)
        {
            if (texture._imageView != VkImageView.Null)
            {
                GraphicsDevice.DeviceAPI.vkDestroyImageView(texture._imageView);
                texture._imageView = VkImageView.Null;
            }

            GraphicsDevice.DeviceAPI.vkCreateImageView(createInfo, null, out texture._imageView).CheckResult( "Create Image View failed!");
            GraphicsDevice.SetObjectName(VkObjectType.ImageView, texture._imageView.Handle, string.Format("{0}_{1}",texture.GetTextureTypeName(), texture.AssetName));
        }

        public static string GetTextureTypeName(this Texture texture)
        {
            if (texture is Texture2D)
            {
                return "TEX2D";
            }
            else if (texture is Texture2DArray)
            {
                return "TEX2D_ARRAY";
            }
            else if (texture is Texture3D)
            {
                return "TEX3D";
            }
            else if (texture is Cubemap)
            {
                return "CUBE";
            }
            else if (texture is CubemapArray)
            {
                return "CUBE_ARRAY";
            }
            else
            {
                return "TEX";
            }

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe void CreateImage(this Texture texture, VkImageCreateInfo createInfo)
        {
            texture.CreateImage(_allocationCreateInfo, createInfo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe void CreateImage(this Texture texture, VmaAllocationCreateInfo allocationCreateInfo, VkImageCreateInfo imageCreateInfo)
        {
            if (texture._vkImage != VkImage.Null && texture._allocation != VmaAllocation.Null)
            {
                Vma.vmaDestroyImage(GraphicsDevice.VmaAllocator, texture._vkImage, texture._allocation);
                texture._vkImage = VkImage.Null;
                texture._allocation = VmaAllocation.Null;
            }
            
            Vma.vmaCreateImage(GraphicsDevice.VmaAllocator, imageCreateInfo, allocationCreateInfo, out texture._vkImage, out texture._allocation).CheckResult( "Create Image View failed!");

            GraphicsDevice.DeviceAPI.vkGetImageMemoryRequirements(texture._vkImage, out var requirements);
            texture._vkBufferSizeRequirement = requirements.size;
            GraphicsDevice.SetObjectName(VkObjectType.Image, texture._vkImage.Handle, texture.AssetName);
        }
        #endregion
        
        #region Image Layout
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static VkImageLayout SetImageLayoutAndAspectFromUsage(this Texture texture)
        {
            var layout = VkImageLayout.Undefined;
            if (texture._useageFlags.HasFlag(VkImageUsageFlags.Sampled))
            {
                texture._aspectFlags = VkImageAspectFlags.Color;
            }
            if (texture._useageFlags.HasFlag(VkImageUsageFlags.ColorAttachment))
            {
                texture._aspectFlags = VkImageAspectFlags.Color;
                layout = VkImageLayout.ColorAttachmentOptimal;
            }
            if (texture._useageFlags.HasFlag(VkImageUsageFlags.DepthStencilAttachment))
            {
                if (texture.Format == VkFormat.S8Uint)
                {
                    texture._aspectFlags = VkImageAspectFlags.Stencil;
                }
                else
                {
                    texture._aspectFlags = VkImageAspectFlags.Depth;
                }
            }
            if (texture.Format == VkFormat.D16UnormS8Uint || texture.Format == VkFormat.D32SfloatS8Uint || texture.Format == VkFormat.D24UnormS8Uint)
            {
                texture._aspectFlags |= VkImageAspectFlags.Stencil;
            }

            return layout;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetImageLayout(Texture texture,VkImageLayout newImageLayout, VkPipelineStageFlags2 srcStage , VkPipelineStageFlags2 dstStage)
        {
            _setLayoutCmdsQueue.Enqueue(new(texture, newImageLayout, srcStage, dstStage));
        }

        internal unsafe static void PlaybackSetLayoutCmds(VkCommandBuffer cmd)
        {
            if (_setLayoutCmdsQueue.IsEmpty) return;
            _setLayoutCmds.Clear();
            _setLayoutCmds.EnsureCapacity(_setLayoutCmdsQueue.Count);
            while (_setLayoutCmdsQueue.TryDequeue(out var layout))
            {
                _setLayoutCmds.Add(layout);
            }

            VkImageMemoryBarrier2* barriers = stackalloc VkImageMemoryBarrier2[_setLayoutCmds.Count];

            for (int i = 0; i < _setLayoutCmds.Count; i++)
            {
                var layout = _setLayoutCmds[i];
                var srcStage = layout.Texture.ImageLayout.GetStageFlagFromLayout();
                barriers[i] = layout.Texture.GetImageLayoutBarrier(layout.NewImageLayout, layout.Texture.GetSubresourceRange(), srcStage, layout.DstStage);
                layout.Texture.SetImageLayoutSilent(layout.NewImageLayout);
            }

            MemoryBarrierHelper.ImageMemoryBarrier(cmd, barriers, (uint)_setLayoutCmds.Count);

        }
        #endregion

        #region Image Copy To/From

        internal static unsafe void CreateHostBuffer(this Texture texture, bool copyFromGPUNow)
        {
            bool createNewBuffer = true;
            if (texture._hostBuffer != null && texture._hostBuffer.VkBufferSize == texture._vkBufferSizeRequirement)
            {
                if (texture._hostBuffer.UsageFlags.HasFlag(VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.TransferDst))
                {
                    if (copyFromGPUNow)
                    {
                        var cmd = GraphicsDevice.BeginSingleTimeMainPipe();
                        texture.CopyToBuffer(cmd, texture._hostBuffer);
                        GraphicsDevice.EndSingleTimeMainPipe(cmd);
                    }
                    else
                    {
                        texture.CopyToBuffer(texture._hostBuffer);
                    }

                    return;
                }
                texture._hostBuffer.EnqueueForDisposal();
                texture._hostBuffer = null;
                createNewBuffer = true;
            }
            else if (texture._hostBuffer != null && texture._hostBuffer.VkBufferSize == texture._vkBufferSizeRequirement)
            {
                if (texture._hostBuffer.UsageFlags.HasFlag(VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.TransferDst) && texture.BufferInstanceSize == texture._hostBuffer.InstanceSize)
                {
                    texture._hostBuffer.Reallocate(texture._vkBufferSizeRequirement / texture.BufferInstanceCount);
                    copyFromGPUNow = true;
                    createNewBuffer = false;
                }
                else
                {
                    texture._hostBuffer.EnqueueForDisposal();
                    texture._hostBuffer = null;
                    createNewBuffer = true;
                }
            }

            if (createNewBuffer || texture._hostBuffer == null)
            {
                texture._hostBuffer = new(texture.BufferInstanceCount, (uint)texture.BufferInstanceSize, VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.TransferDst, true, false, false);
            }
            if (copyFromGPUNow)
            {
                var cmd = GraphicsDevice.BeginSingleTimeMainPipe();
                texture.CopyToBuffer(cmd, texture._hostBuffer);
                GraphicsDevice.EndSingleTimeMainPipe(cmd);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyFromArray<T>(this Texture texture, T[] colours) where T : unmanaged
        {
            var stagingBuffer = new GPUBuffer<T>((ulong)colours.Length, VkBufferUsageFlags.TransferSrc, true, false, false);

            stagingBuffer.WriteToBuffer(colours);

            texture.CopyFromBuffer(stagingBuffer, true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CopyFromBuffer(this Texture texture, GPUBuffer buffer, bool disposeBufferAfterCopy = false, bool disallowMipMapRegen = false)
        {
            _copyBufferToTexture.Enqueue(new(texture, buffer, disposeBufferAfterCopy, disallowMipMapRegen));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CopyFromBuffer(this Texture texture, GPUBuffer buffer, ulong[] offsets, VkExtent3D[] extents, bool disposeBufferAfterCopy = false)
        {
            _copyBufferToTexture.Enqueue(new(texture, buffer, disposeBufferAfterCopy, offsets,extents));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CopyFromBuffer(this Texture texture, GPUBuffer buffer, VkBufferImageCopy copyRegion, bool disposeBufferAfterCopy = false)
        {
            _copyBufferToTexture.Enqueue(new(texture, buffer, copyRegion, disposeBufferAfterCopy));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CopyFromBuffer(this Texture texture, GPUBuffer buffer, VkBufferImageCopy[] copyCmds, bool disposeBufferAfterCopy = false)
        {
            _copyBufferToTexture.Enqueue(new(texture, buffer, copyCmds, disposeBufferAfterCopy));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CopyFrombufferNow(this Texture texture, GPUBuffer buffer)
        {
            var cmd = GraphicsDevice.BeginSingleTimeMainPipe();
            bool hintRegenerateMipMaps = CopyFromBuffer(texture, cmd, buffer);
            if (hintRegenerateMipMaps && texture.MipMapCount > 1)
            {
                texture.RegenerateMipMaps(cmd);
                Console.WriteLine("Regenerating mipmaps for texture");
            }
            else if (!hintRegenerateMipMaps && texture.MipMapCount > 1)
            {
                Console.WriteLine("Skipped mipmaps regeneration for texture");
            }
            GraphicsDevice.EndSingleTimeMainPipe(cmd);
        }

        internal static unsafe void PlaybackCopyCmds(VkCommandBuffer cmd)
        {
            VkBufferImageCopy copyCmd;
            while (_copyBufferToTexture.TryDequeue(out var copy))
            {
                if (copy.Buffer.IsDisposed || copy.Texture.IsDisposed) continue;
                bool hintRegenerateMipMaps = false;
                if (copy.DirectCopyCmd)
                {
                    if (copy.CopyCmds != null)
                    {
                        fixed(VkBufferImageCopy* copyCmds = copy.CopyCmds)
                        {
                            CopyBufferToTexture(copy.Texture, cmd, copy.Buffer, (uint)copy.CopyCmds.Length, copyCmds);
                        }
                    }
                    else
                    {
                        copyCmd = copy.CopyCmd;
                        CopyBufferToTexture(copy.Texture, cmd, copy.Buffer, 1, &copyCmd);
                    }
                    
                }
                else
                {
                    hintRegenerateMipMaps = CopyFromBuffer(copy.Texture, cmd, copy.Buffer, copy.Offsets, copy.Extents);
                }
                
                if (!copy.DisallowMipMapRegen && hintRegenerateMipMaps && copy.Texture.MipMapCount > 1)
                {
                    _regenMipMapsCmds.Enqueue(copy.Texture);
                }
                if (copy.DisposeBufferAfterCopy)
                {
                    copy.Buffer.EnqueueForDisposal();
                }
            }

            while (_copyTextureToBuffer.TryDequeue(out var copy))
            {
                if (copy.Buffer.IsDisposed || copy.Texture.IsDisposed) continue;
                copy.Texture.CopyToBuffer(cmd, copy.Buffer);
            }
        }

        internal static unsafe bool CopyFromBuffer(this Texture texture, VkCommandBuffer cmdBuffer, GPUBuffer buffer, ulong[] offsets = null, VkExtent3D[] extents = null)
        {
            var imageLayout = texture.ImageLayout;
            bool changeLayout = false;
            bool hintRegenerateMipMaps = false;
            if (texture.ImageLayout != VkImageLayout.TransferDstOptimal)
            {
                if (texture.ImageLayout == VkImageLayout.TransferSrcOptimal)
                {
                    texture.SetImageLayout(cmdBuffer, VkImageLayout.TransferDstOptimal, VkPipelineStageFlags2.Transfer, VkPipelineStageFlags2.Transfer);
                }
                else
                {
                    texture.SetImageLayout(cmdBuffer, VkImageLayout.TransferDstOptimal, VkPipelineStageFlags2.FragmentShader, VkPipelineStageFlags2.Transfer);
                }
                changeLayout = true;
            }

            var subresourceRange = texture.GetSubresourceRange();


            ulong offset = 0;
            uint formatSize = (uint)Vulkan.BlockSize(texture.Format);

            uint baseImageSize = texture.ImageExtent.width * texture.ImageExtent.height * formatSize;

            if(texture is Cubemap cubemap)
            {
                uint copyCount = texture.MipMapCount * 6;
                bool copyingMipMaps = false;
                if (offsets != null && copyCount != offsets.Length)
                {
                    offsets = null;
                    extents = null;

                }
                else
                {
                    copyingMipMaps = offsets != null;
                }

                if (buffer.VkBufferSize <= baseImageSize && !copyingMipMaps)
                {
                    copyCount = 6;
                    hintRegenerateMipMaps = true;
                }

                VkBufferImageCopy* bufferCopyRegions = stackalloc VkBufferImageCopy[(int)copyCount];
                for (uint i = 0, k = 0; i < copyCount; i++)
                {
                    for (uint j = 0; j < texture.MipMapCount; j++, k++)
                    {
                        bufferCopyRegions[i] = new()
                        {
                            bufferOffset = offsets == null ? offset : offsets[k],
                            bufferRowLength = 0,
                            bufferImageHeight = 0,
                            imageSubresource = new()
                            {
                                aspectMask = subresourceRange.aspectMask,
                                mipLevel = j,
                                baseArrayLayer = i,
                                layerCount = 1
                            },
                            imageOffset = new(0, 0, 0),
                            imageExtent = extents == null
                            ? new(texture.ImageExtent.width, texture.ImageExtent.height, 1)
                            : new(extents[0].width, extents[0].height, 1)
                        };
                        offset += baseImageSize;
                    }
                }
                CopyBufferToTexture(texture, cmdBuffer, buffer,copyCount, bufferCopyRegions);
            }
            else if (texture is Texture2DArray textureArray)
            {
                uint copyCount = texture.MipMapCount;
                bool copyingMipMaps = false;
                if (offsets != null && copyCount * textureArray.Depth != offsets.Length)
                {
                    offsets = null;
                    extents = null;

                }
                else
                {
                    copyingMipMaps = offsets != null;
                }

                if (buffer.VkBufferSize <= baseImageSize && !copyingMipMaps)
                {
                    copyCount = 1;
                    hintRegenerateMipMaps = true;
                }

                VkBufferImageCopy* bufferCopyRegions = stackalloc VkBufferImageCopy[(int)texture.ImageExtent.depth * (int)texture.MipMapCount];
                for (uint i = 0, k = 0; i < texture.ImageExtent.depth; i++)
                {
                    for (uint j = 0; j < texture.MipMapCount; j++, k++)
                    {
                        bufferCopyRegions[k] = new()
                        {
                            bufferOffset = offsets == null ? offset : offsets[k],
                            bufferRowLength = 0,
                            bufferImageHeight = 0,
                            imageSubresource = new()
                            {
                                aspectMask = subresourceRange.aspectMask,
                                mipLevel = j,
                                baseArrayLayer = i,
                                layerCount = 1
                            },
                            imageOffset = new(0, 0, 0),
                            imageExtent = extents == null 
                            ? new(texture.ImageExtent.width, texture.ImageExtent.height, 1)
                            : new(extents[i].width,extents[i].height, 1)
                        };
                        offset += baseImageSize;
                    }
                }
                CopyBufferToTexture(texture, cmdBuffer, buffer, texture.ImageExtent.depth * texture.MipMapCount, bufferCopyRegions);
                
            }
            else if (texture is Texture2D texture2D)
            {
                uint copyCount = texture.MipMapCount;
                bool copyingMipMaps = false;
                if (offsets != null && copyCount != offsets.Length)
                {
                    offsets = null;
                    extents = null;
                    
                }
                else
                {
                    copyingMipMaps = offsets != null;
                }

                if (buffer.VkBufferSize <= baseImageSize && !copyingMipMaps)
                {
                    copyCount = 1;
                    hintRegenerateMipMaps = true;
                }

                VkBufferImageCopy* bufferCopyRegions = stackalloc VkBufferImageCopy[(int)copyCount];
                for (uint i = 0; i < copyCount; i++)
                {
                    bufferCopyRegions[i] = new()
                    {
                        bufferOffset = offsets == null ? offset : offsets[i],
                        bufferRowLength = 0,
                        bufferImageHeight = 0,
                        imageSubresource = new()
                        {
                            aspectMask = subresourceRange.aspectMask,
                            mipLevel = i,
                            baseArrayLayer = 0,
                            layerCount = 1
                        },
                        imageOffset = new(
                            0,
                            0,
                            0
                        ),
                        imageExtent = extents == null 
                        ? new((int)(texture.ImageExtent.width >> (int)i), (int)(texture.ImageExtent.height >> (int)i), 1)
                        : extents[i]
                    };

                    baseImageSize = bufferCopyRegions[i].imageExtent.width * bufferCopyRegions[i].imageExtent.height * formatSize;
                    offset += baseImageSize;
                }

                CopyBufferToTexture(texture, cmdBuffer, buffer,copyCount, bufferCopyRegions);
            }
            else
            {
                throw new NotImplementedException(string.Format("Copy from buffer not implemented for {0}", texture.GetType().FullName));
            }

            if (changeLayout && imageLayout != VkImageLayout.Undefined)
            {
                if (imageLayout == VkImageLayout.TransferSrcOptimal)
                {
                    texture.SetImageLayout(cmdBuffer, imageLayout, VkPipelineStageFlags2.Transfer, VkPipelineStageFlags2.Transfer);
                }
                else
                {
                    texture.SetImageLayout(cmdBuffer, imageLayout, VkPipelineStageFlags2.Transfer);
                }
            }
            return hintRegenerateMipMaps;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void CopyBufferToTexture(Texture texture, VkCommandBuffer cmdBuffer, GPUBuffer buffer, uint copyCount, VkBufferImageCopy* bufferCopyRegions)
        {
            VkImageLayout setBackLayout = VkImageLayout.Undefined;
            if(texture.ImageLayout != VkImageLayout.TransferDstOptimal)
            {
                setBackLayout = texture.ImageLayout;
                texture.SetImageLayout(cmdBuffer,VkImageLayout.TransferDstOptimal, VkPipelineStageFlags2.FragmentShader, VkPipelineStageFlags2.Transfer);
            }
            GraphicsDevice.DeviceAPI.vkCmdCopyBufferToImage(cmdBuffer, buffer.VkBuffer, texture._vkImage, VkImageLayout.TransferDstOptimal, copyCount, bufferCopyRegions);
            if(setBackLayout != VkImageLayout.Undefined)
            {
                texture.SetImageLayout(cmdBuffer, setBackLayout, VkPipelineStageFlags2.Transfer, VkPipelineStageFlags2.FragmentShader);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CopyToBuffer(this Texture texture, GPUBuffer buffer)
        {
            _copyTextureToBuffer.Enqueue(new(texture, buffer, false,false));
        }

        internal static unsafe void CopyToBuffer(this Texture texture, VkCommandBuffer cmdBuffer, GPUBuffer buffer)
        {
            var imageLayout = texture.ImageLayout;
            bool changeLayout = false;
            if (texture.ImageLayout != (VkImageLayout.TransferSrcOptimal))
            {
                texture.SetImageLayout(cmdBuffer, VkImageLayout.TransferSrcOptimal, VkPipelineStageFlags2.Transfer, VkPipelineStageFlags2.Transfer);
                changeLayout = true;
            }

            ulong offset = 0;
            uint formatSize = (uint)Vulkan.BlockSize(texture.Format);
            uint size = texture.ImageExtent.width * texture.ImageExtent.height * formatSize;
            var subresourceRange = texture.GetSubresourceRange();
            VkBufferImageCopy* bufferCopyRegions = stackalloc VkBufferImageCopy[(int)texture.MipMapCount];
            for (uint i = 0; i < texture.MipMapCount; i++)
            {
                bufferCopyRegions[i] = new()
                {
                    bufferOffset = offset,
                    bufferRowLength = 0,
                    bufferImageHeight = 0,
                    imageSubresource = new()
                    {
                        aspectMask = subresourceRange.aspectMask,
                        mipLevel = i,
                        baseArrayLayer = 0,
                        layerCount = 1
                    },
                    imageOffset = new(
                        0,
                        0,
                        0
                    ),
                    imageExtent = new(
                        (int)(texture.ImageExtent.width >> (int)i),
                        (int)(texture.ImageExtent.height >> (int)i),
                        1
                    )
                };

                var region = bufferCopyRegions[i];

                size = bufferCopyRegions[i].imageExtent.width * bufferCopyRegions[i].imageExtent.height * formatSize;
                offset += size;
            }

            GraphicsDevice.DeviceAPI.vkCmdCopyImageToBuffer(cmdBuffer, texture._vkImage, VkImageLayout.TransferSrcOptimal, buffer.VkBuffer, texture.MipMapCount, bufferCopyRegions);

            if (changeLayout && imageLayout != VkImageLayout.Undefined)
            {
                texture.SetImageLayout(cmdBuffer, imageLayout, VkPipelineStageFlags2.Transfer, VkPipelineStageFlags2.Transfer);
            }
        }

        #endregion

        #region MipMap Generation
        internal static void PlaybackMipmapGenCmds(VkCommandBuffer cmd)
        {
            while (_regenMipMapsCmds.TryDequeue(out var texture))
            {
                if (texture.IsDisposed) continue;
                Debug.Assert(texture.MipMapCount > 1, "Attempting regenerate mipmaps for texture with no mipmaps!");
                texture.RegenerateMipMaps(cmd);
            }
        }

        public static unsafe void GenerateMipMaps(this Texture2D texture, VkCommandBuffer cmd)
        {
            var subresourceRange = texture.GetSubresourceRange();
            var imageLayout = texture.ImageLayout;
            uint queueFamily = GraphicsDevice.PhysicalQueueFamilies.graphicsFamily;

            if (texture._imageLayout == VkImageLayout.ShaderReadOnlyOptimal)
            {
                texture.SetImageLayout(cmd, VkImageLayout.TransferSrcOptimal, subresourceRange, VkPipelineStageFlags2.FragmentShader, VkPipelineStageFlags2.Transfer);
            }
            else if (texture._imageLayout == VkImageLayout.General)
            {
                texture.SetImageLayout(cmd, VkImageLayout.TransferSrcOptimal, subresourceRange, VkPipelineStageFlags2.ComputeShader, VkPipelineStageFlags2.Transfer);
            }
            else
            {
                texture.SetImageLayout(cmd, VkImageLayout.TransferSrcOptimal, subresourceRange, VkPipelineStageFlags2.Transfer, VkPipelineStageFlags2.Transfer);
            }

            for (uint i = 1; i < texture.MipMapCount; i++)
            {
                VkImageBlit imageBlit = new()
                {
                    srcSubresource = new()
                    {
                        aspectMask = subresourceRange.aspectMask,
                        layerCount = subresourceRange.layerCount,
                        mipLevel = i - 1,
                    },
                    dstSubresource = new()
                    {
                        aspectMask = subresourceRange.aspectMask,
                        layerCount = subresourceRange.layerCount,
                        mipLevel = i
                    }
                };

                imageBlit.srcOffsets[1].x = Math.Max(1, (int)(texture.ImageExtent.width >> (int)(i - 1)));
                imageBlit.srcOffsets[1].y = Math.Max(1, (int)(texture.ImageExtent.height >> (int)(i - 1)));
                imageBlit.srcOffsets[1].z = 1;

                imageBlit.dstOffsets[1].x = Math.Max(1, (int)(texture.ImageExtent.width >> (int)i));
                imageBlit.dstOffsets[1].y = Math.Max(1, (int)(texture.ImageExtent.height >> (int)i));
                imageBlit.dstOffsets[1].z = 1;


                VkImageSubresourceRange mipSubRange = new(
                    subresourceRange.aspectMask,
                    i,
                    1,
                    subresourceRange.baseArrayLayer,
                    subresourceRange.layerCount
                );

                MemoryBarrierHelper.ImageMemoryBarrier(
                    cmd,
                    texture._vkImage,
                    mipSubRange,
                    VkPipelineStageFlags2.Transfer,
                    VkAccessFlags2.None,
                    VkPipelineStageFlags2.Transfer,
                    VkAccessFlags2.TransferWrite,
                    VkImageLayout.Undefined,
                    VkImageLayout.TransferDstOptimal,
                    queueFamily, queueFamily
                );

                BlitGeneric(
                    cmd,
                    VkFilter.Linear,
                    imageBlit,
                    texture._vkImage,
                    VkImageLayout.TransferSrcOptimal,
                    texture._vkImage,
                    VkImageLayout.TransferDstOptimal
                );


                MemoryBarrierHelper.ImageMemoryBarrier(
                    cmd,
                    texture._vkImage,
                    mipSubRange,
                    VkPipelineStageFlags2.Transfer,
                    VkAccessFlags2.TransferWrite,
                    VkPipelineStageFlags2.Transfer,
                    VkAccessFlags2.TransferRead,
                    VkImageLayout.TransferDstOptimal,
                    VkImageLayout.TransferSrcOptimal,
                    queueFamily, queueFamily
                );
            }

            texture._imageLayout = VkImageLayout.TransferSrcOptimal;

            if(imageLayout == VkImageLayout.TransferDstOptimal)
            {
                texture.SetImageLayout(cmd, imageLayout, VkPipelineStageFlags2.Transfer, VkPipelineStageFlags2.Transfer);
            }
            else if(imageLayout == VkImageLayout.General)
            {
                texture.SetImageLayout(cmd,imageLayout,VkPipelineStageFlags2.Transfer,VkPipelineStageFlags2.ComputeShader);
            }
            else if (imageLayout != VkImageLayout.Undefined)
            {
                texture.SetImageLayout(cmd, imageLayout, VkPipelineStageFlags2.Transfer);
            }
        }

        public static unsafe void GenerateMipMaps(this Texture3D texture, VkCommandBuffer cmd)
        {
            var subresourceRange = texture.GetSubresourceRange();
            var imageLayout = texture.ImageLayout;
            uint queueFamily = GraphicsDevice.PhysicalQueueFamilies.graphicsFamily;

            texture.SetImageLayout(cmd, VkImageLayout.TransferSrcOptimal, subresourceRange);

            for (uint i = 1; i < texture.MipMapCount; i++)
            {
                VkImageBlit imageBlit = new()
                {
                    srcSubresource = new()
                    {
                        aspectMask = subresourceRange.aspectMask,
                        layerCount = subresourceRange.layerCount,
                        mipLevel = i - 1,
                    },
                    dstSubresource = new()
                    {
                        aspectMask = subresourceRange.aspectMask,
                        layerCount = subresourceRange.layerCount,
                        mipLevel = i
                    }
                };

                imageBlit.srcOffsets[1].x = (int)(texture.ImageExtent.width >> (int)(i - 1));
                imageBlit.srcOffsets[1].y = (int)(texture.ImageExtent.height >> (int)(i - 1));
                imageBlit.srcOffsets[1].z = (int)(texture.ImageExtent.depth >> (int)i);

                imageBlit.dstOffsets[1].x = (int)(texture.ImageExtent.width >> (int)i);
                imageBlit.dstOffsets[1].y = (int)(texture.ImageExtent.height >> (int)i);
                imageBlit.dstOffsets[1].z = (int)(texture.ImageExtent.depth >> (int)i);

                VkImageSubresourceRange mipSubRange = new(
                    subresourceRange.aspectMask,
                    i,
                    1,
                    subresourceRange.baseArrayLayer,
                    subresourceRange.layerCount
                );

                MemoryBarrierHelper.ImageMemoryBarrier(
                    cmd,
                    texture._vkImage,
                    mipSubRange,
                    VkPipelineStageFlags2.Transfer,
                    VkAccessFlags2.None,
                    VkPipelineStageFlags2.Transfer,
                    VkAccessFlags2.TransferWrite,
                    VkImageLayout.Undefined,
                    VkImageLayout.TransferDstOptimal,
                    queueFamily, queueFamily
                );

                BlitGeneric(
                    cmd,
                    VkFilter.Linear,
                    imageBlit,
                    texture._vkImage,
                    VkImageLayout.TransferSrcOptimal,
                    texture._vkImage,
                    VkImageLayout.TransferDstOptimal);

                MemoryBarrierHelper.ImageMemoryBarrier(
                    cmd,
                    texture._vkImage,
                    mipSubRange,
                    VkPipelineStageFlags2.Transfer,
                    VkAccessFlags2.TransferWrite,
                    VkPipelineStageFlags2.Transfer,
                    VkAccessFlags2.TransferRead,
                    VkImageLayout.TransferDstOptimal,
                    VkImageLayout.TransferSrcOptimal,
                    queueFamily, queueFamily
                );
            }

            texture._imageLayout = VkImageLayout.TransferSrcOptimal;


            if (imageLayout == VkImageLayout.TransferDstOptimal)
            {
                texture.SetImageLayout(cmd, imageLayout, VkPipelineStageFlags2.Transfer, VkPipelineStageFlags2.Transfer);
            }
            else if (imageLayout != VkImageLayout.Undefined)
            {
                texture.SetImageLayout(cmd, imageLayout, VkPipelineStageFlags2.Transfer);
            }
        }

        public static unsafe void GenerateMipMaps(this Texture2DArray texture, VkCommandBuffer cmd)
        {
            var subresourceRange = texture.GetSubresourceRange();
            var imageLayout = texture.ImageLayout;

            if (texture._imageLayout == VkImageLayout.ShaderReadOnlyOptimal)
            {
                texture.SetImageLayout(cmd, VkImageLayout.TransferSrcOptimal, subresourceRange, VkPipelineStageFlags2.FragmentShader, VkPipelineStageFlags2.Transfer);
            }
            else
            {
                texture.SetImageLayout(cmd, VkImageLayout.TransferSrcOptimal, subresourceRange, VkPipelineStageFlags2.Transfer, VkPipelineStageFlags2.Transfer);
            }

            GenerateMipMapsTextureArrayCubemap(cmd, texture._vkImage, texture.ImageExtent.depth, 0, texture.MipMapCount, texture.ImageExtent, texture._aspectFlags);

            texture._imageLayout = VkImageLayout.TransferSrcOptimal;


            if (imageLayout == VkImageLayout.TransferDstOptimal)
            {
                texture.SetImageLayout(cmd, imageLayout, VkPipelineStageFlags2.Transfer, VkPipelineStageFlags2.Transfer);
            }
            else if (imageLayout != VkImageLayout.Undefined)
            {
                texture.SetImageLayout(cmd, imageLayout, VkPipelineStageFlags2.Transfer);
            }
        }

        public static unsafe void GenerateMipMaps(this Cubemap texture, VkCommandBuffer cmd)
        {
            var subresourceRange = texture.GetSubresourceRange();
            var imageLayout = texture.ImageLayout;

            if (texture._imageLayout == VkImageLayout.ShaderReadOnlyOptimal)
            {
                texture.SetImageLayout(cmd, VkImageLayout.TransferSrcOptimal, subresourceRange, VkPipelineStageFlags2.FragmentShader, VkPipelineStageFlags2.Transfer);
            }
            else
            {
                texture.SetImageLayout(cmd, VkImageLayout.TransferSrcOptimal, subresourceRange, VkPipelineStageFlags2.Transfer, VkPipelineStageFlags2.Transfer);
            }

            GenerateMipMapsTextureArrayCubemap(cmd, texture._vkImage, 6,0, texture.MipMapCount, texture.ImageExtent, texture._aspectFlags);
            

            texture._imageLayout = VkImageLayout.TransferSrcOptimal;

            if (imageLayout == VkImageLayout.TransferDstOptimal)
            {
                texture.SetImageLayout(cmd, imageLayout, VkPipelineStageFlags2.Transfer, VkPipelineStageFlags2.Transfer);
            }
            else if (imageLayout != VkImageLayout.Undefined)
            {
                texture.SetImageLayout(cmd, imageLayout, VkPipelineStageFlags2.Transfer);
            }
        }

        public static unsafe void GenerateMipMaps(this CubemapArray texture, VkCommandBuffer cmd)
        {
            var subresourceRange = texture.GetSubresourceRange();
            var imageLayout = texture.ImageLayout;
            texture.SetImageLayout(cmd, VkImageLayout.TransferSrcOptimal, subresourceRange);

            for (uint i = 0; i < texture.ImageExtent.depth; i++)
            {
                GenerateMipMapsTextureArrayCubemap(cmd, texture._vkImage, 6, i * 6, texture.MipMapCount, texture.ImageExtent, texture._aspectFlags);
            }
            
            

            texture._imageLayout = VkImageLayout.TransferSrcOptimal;

            if (imageLayout != VkImageLayout.Undefined)
            {
                texture.SetImageLayout(cmd, imageLayout);
            }
        }

        private unsafe static void GenerateMipMapsTextureArrayCubemap(VkCommandBuffer cmd, VkImage image, uint arrayLayers, uint layerOffset, uint mipMapCount, VkExtent3D exetents, VkImageAspectFlags aspectMask)
        {
            uint queueFamily = GraphicsDevice.PhysicalQueueFamilies.graphicsFamily;
            for (uint d = 0; d < arrayLayers; d++)
            {
                for (uint i = 1; i < mipMapCount; i++)
                {
                    VkImageBlit imageBlit = new()
                    {
                        srcSubresource = new()
                        {
                            aspectMask = aspectMask,
                            layerCount = 1,
                            mipLevel = i - 1,
                            baseArrayLayer = layerOffset + d
                        },
                        dstSubresource = new()
                        {
                            aspectMask = aspectMask,
                            layerCount = 1,
                            mipLevel = i,
                            baseArrayLayer = layerOffset + d
                        }
                    };

                    imageBlit.srcOffsets[1].x = (int)(exetents.width >> (int)(i - 1));
                    imageBlit.srcOffsets[1].y = (int)(exetents.height >> (int)(i - 1));
                    imageBlit.srcOffsets[1].z = 1;

                    imageBlit.dstOffsets[1].x = (int)(exetents.width >> (int)i);
                    imageBlit.dstOffsets[1].y = (int)(exetents.height >> (int)i);
                    imageBlit.dstOffsets[1].z = 1;

                    VkImageSubresourceRange mipSubRange = new(
                        aspectMask,
                        i,
                        1,
                        layerOffset + d,
                        1
                    );

                    MemoryBarrierHelper.ImageMemoryBarrier(
                        cmd,
                        image,
                        mipSubRange,
                        VkPipelineStageFlags2.Transfer,
                        VkAccessFlags2.None,
                        VkPipelineStageFlags2.Transfer,
                        VkAccessFlags2.TransferWrite,
                        VkImageLayout.Undefined,
                        VkImageLayout.TransferDstOptimal,
                        queueFamily, queueFamily
                    );

                    BlitGeneric(
                        cmd,
                        VkFilter.Linear,
                        imageBlit,
                        image,
                        VkImageLayout.TransferSrcOptimal,
                        image,
                        VkImageLayout.TransferDstOptimal
                    );

                    MemoryBarrierHelper.ImageMemoryBarrier(
                        cmd,
                        image,
                        mipSubRange,
                        VkPipelineStageFlags2.Transfer,
                        VkAccessFlags2.TransferWrite,
                        VkPipelineStageFlags2.Transfer,
                        VkAccessFlags2.TransferRead,
                        VkImageLayout.TransferDstOptimal,
                        VkImageLayout.TransferSrcOptimal,
                        queueFamily, queueFamily
                    );
                }
            }
        }
        #endregion

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void BlitGeneric(VkCommandBuffer commandBuffer, VkFilter blitFilter, VkImageBlit blit, VkImage src, VkImageLayout srcLayout, VkImage dst, VkImageLayout dstLayout)
        {
            GraphicsDevice.DeviceAPI.vkCmdBlitImage(
                commandBuffer,
                src,
                srcLayout,
                dst,
                dstLayout,
                1,
                &blit,
                blitFilter
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SingleTexture AsSingleTexture(this Texture texture)
        {
            return (SingleTexture)texture;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static VkImageBlit GetBlitCmd(this Texture texture, int dstWidth, int dstHeight, VkImageAspectFlags dstAspectMask)
        {
            VkImageBlit imageBlit = new()
            {
                srcSubresource = new()
                {
                    aspectMask = dstAspectMask,
                    layerCount = 1,
                    mipLevel = 0,

                },
                dstSubresource = new()
                {
                    layerCount = 1,
                    mipLevel = 0
                }
            };
            if (texture._aspectFlags.HasFlag(VkImageAspectFlags.Color))
            {
                imageBlit.dstSubresource.aspectMask = VkImageAspectFlags.Color;
            }
            else if (texture._aspectFlags.HasFlag(VkImageAspectFlags.Depth) && texture._aspectFlags.HasFlag(VkImageAspectFlags.Stencil))
            {
                imageBlit.dstSubresource.aspectMask = VkImageAspectFlags.Depth | VkImageAspectFlags.Stencil;
            }
            else if (texture._aspectFlags.HasFlag(VkImageAspectFlags.Depth))
            {
                imageBlit.dstSubresource.aspectMask = VkImageAspectFlags.Depth;
            }
            else if (texture._aspectFlags.HasFlag(VkImageAspectFlags.Stencil))
            {
                imageBlit.dstSubresource.aspectMask = VkImageAspectFlags.Stencil;
            }
            imageBlit.srcOffsets[1].x = texture.Width;
            imageBlit.srcOffsets[1].y = texture.Height;
            imageBlit.srcOffsets[1].z = 1;

            imageBlit.dstOffsets[1].x = dstWidth;
            imageBlit.dstOffsets[1].y = dstHeight;
            imageBlit.dstOffsets[1].z = 1;

            return imageBlit;
        }
    }
}