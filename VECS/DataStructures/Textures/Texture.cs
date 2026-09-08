using System;
using System.Diagnostics;
using VECS.LowLevel;
using Vortice.Vulkan;

namespace VECS
{
    public abstract class Texture : DisposableAsset
    {
        private int _anisoLevel;
        protected VkExtent3D _imageExtent;
        protected VkFormat _imageFormat = VkFormat.R8G8B8A8Unorm;
        internal VkImage _vkImage = VkImage.Null;
        protected bool _readable = false;
        private uint _mipMapCount = 1;
        protected uint _baseMipMapLevel = 0;
        internal ulong _vkBufferSizeRequirement;

        internal VkImageLayout _imageLayout = VkImageLayout.Undefined;
        protected VkImageViewType _imageImageViewType;
        protected VkImageTiling _imageTiling = VkImageTiling.Optimal;
        internal VkImageUsageFlags _useageFlags = VkImageUsageFlags.TransferDst | VkImageUsageFlags.TransferSrc | VkImageUsageFlags.Sampled;
        protected VkSampleCountFlags _sampleCountFlags = VkSampleCountFlags.Count1;
        protected VkSharingMode _sharingMode = VkSharingMode.Exclusive;
        protected uint[] _queueFamilyIndices = null;
        internal VmaAllocation _allocation = VmaAllocation.Null;

        // image view
        protected VkImageViewCreateFlags _imageViewCreateFlags = VkImageViewCreateFlags.None;
        internal VkComponentMapping _swizzle = VkComponentMapping.Identity;
        internal VkImageAspectFlags _aspectFlags = VkImageAspectFlags.Color;
        internal VkImageView _imageView;

        protected internal GPUBuffer _hostBuffer;

        protected VkDescriptorImageInfo _imageInfo;

        // sampler

        internal TextureSampler _textureSampler;
        internal TextureMetaFile TextureMetaFile => (TextureMetaFile)MetaFile;
        internal bool _regenerateSampler;

        public uint MipMapCount
        {
            get => _mipMapCount;
            protected set
            {
                _mipMapCount = value;
                MaxMipLOD = _mipMapCount;
            }
        }

        public int AnisoLevel
        {
            get => _anisoLevel;
            protected set
            {
                _anisoLevel = value;
                AnisotropyEnable = _anisoLevel > 0;
                
            }
        }


        public VkSampler TextureSampler
        {
            get
            {
                if (_textureSampler == null)
                {
                    return VkSampler.Null;
                }
                return _textureSampler.VkSampler;
            }
        }


        // descriptor

        // properties

        protected VkSamplerCreateInfo _samplerCreateInfo = new()
        {
            minFilter = VkFilter.Linear,
            magFilter = VkFilter.Linear,
            addressModeU = VkSamplerAddressMode.Repeat,
            addressModeV = VkSamplerAddressMode.Repeat,
            addressModeW = VkSamplerAddressMode.Repeat,
            borderColor = VkBorderColor.IntOpaqueBlack,
            unnormalizedCoordinates = false,
            compareEnable = true,
            compareOp = VkCompareOp.Always,
            mipmapMode = VkSamplerMipmapMode.Linear
        };

        public VkFilter MinFilter
        {
            get => _samplerCreateInfo.minFilter;
            set
            {
                if (_samplerCreateInfo.minFilter != value)
                {
                    _samplerCreateInfo.minFilter = value;
                    _regenerateSampler = true;
                }
            }
        }
        public VkFilter MagFilter
        {
            get => _samplerCreateInfo.magFilter;
            set
            {
                if (_samplerCreateInfo.magFilter != value)
                {
                    _samplerCreateInfo.magFilter = value;
                    _regenerateSampler = true;
                }
            }
        }
        public VkSamplerAddressMode WrapModeU
        {
            get => _samplerCreateInfo.addressModeU;
            set
            {
                if (_samplerCreateInfo.addressModeU != value)
                {
                    _samplerCreateInfo.addressModeU = value;
                    _regenerateSampler = true;
                }
            }
        }
        public VkSamplerAddressMode WrapModeV
        {
            get => _samplerCreateInfo.addressModeV;
            set
            {
                if (_samplerCreateInfo.addressModeV != value)
                {
                    _samplerCreateInfo.addressModeV = value;
                    _regenerateSampler = true;
                }
            }
        }
        public VkSamplerAddressMode WrapModeW
        {
            get => _samplerCreateInfo.addressModeW;
            set
            {
                if (_samplerCreateInfo.addressModeW != value)
                {
                    _samplerCreateInfo.addressModeW = value;
                    _regenerateSampler = true;
                }
            }
        }
        public bool AnisotropyEnable
        {
            get => _samplerCreateInfo.anisotropyEnable;
            set
            {
                if (_samplerCreateInfo.anisotropyEnable != value)
                {
                    _samplerCreateInfo.anisotropyEnable = value;
                    _regenerateSampler = true;
                }
            }
        }
        public float MaxAnisotropy
        {
            get => _samplerCreateInfo.maxAnisotropy;
            set
            {
                value = Math.Max(1, Math.Max(_samplerCreateInfo.maxAnisotropy, Math.Min(GraphicsDevice.PropertiesVK10.limits.maxSamplerAnisotropy, value)));
                if (_samplerCreateInfo.maxAnisotropy != value)
                {
                    _samplerCreateInfo.maxAnisotropy = value;
                    _regenerateSampler = true;
                }
            }
        }
        public VkBorderColor BorderColour
        {
            get => _samplerCreateInfo.borderColor;
            set
            {
                if (_samplerCreateInfo.borderColor != value)
                {
                    _samplerCreateInfo.borderColor = value;
                    _regenerateSampler = true;
                }
            }
        }
        public bool UnnormalisedCoordinates
        {
            get => _samplerCreateInfo.unnormalizedCoordinates;
            set
            {
                if (_samplerCreateInfo.unnormalizedCoordinates != value)
                {
                    _samplerCreateInfo.unnormalizedCoordinates = value;
                    if (value)
                    {
                        MinMipLOD = 0;
                        MaxMipLOD = 0;
                        MipMapMode = VkSamplerMipmapMode.Nearest;
                        MinFilter = MagFilter;
                        WrapModeU = VkSamplerAddressMode.ClampToEdge;
                        WrapModeV = VkSamplerAddressMode.ClampToEdge;
                        WrapModeW = VkSamplerAddressMode.ClampToEdge;
                        CompareEnable = false;
                    }
                    _regenerateSampler = true;
                }
            }
        }
        public bool CompareEnable
        {
            get => _samplerCreateInfo.compareEnable;
            set
            {
                if (_samplerCreateInfo.compareEnable != value)
                {
                    _samplerCreateInfo.compareEnable = value;
                    _regenerateSampler = true;
                }
            }
        }
        public VkCompareOp CompareOp
        {
            get => _samplerCreateInfo.compareOp;
            set
            {
                if (_samplerCreateInfo.compareOp != value)
                {
                    _samplerCreateInfo.compareOp = value;
                    _regenerateSampler = true;
                }
            }
        }
        public VkSamplerMipmapMode MipMapMode
        {
            get => _samplerCreateInfo.mipmapMode;
            set
            {
                if (_samplerCreateInfo.mipmapMode != value)
                {
                    _samplerCreateInfo.mipmapMode = value;
                    _regenerateSampler = true;
                }
            }
        }
        public float MipMapBias
        {
            get => _samplerCreateInfo.mipLodBias;
            set
            {
                value = Math.Min(GraphicsDevice.PropertiesVK10.limits.maxSamplerLodBias, value);
                if (_samplerCreateInfo.mipLodBias != value)
                {
                    _samplerCreateInfo.mipLodBias = value;
                    _regenerateSampler = true;
                }
            }
        }
        public float MinMipLOD
        {
            get => _samplerCreateInfo.minLod;
            set
            {
                if (_samplerCreateInfo.minLod != value)
                {
                    _samplerCreateInfo.minLod = Math.Max(0, value);
                    _regenerateSampler = true;
                }
            }
        }
        public float MaxMipLOD
        {
            get => _samplerCreateInfo.maxLod;
            set
            {
                value = Math.Max(MinMipLOD, value);
                if (_samplerCreateInfo.maxLod != value)
                {
                    _samplerCreateInfo.maxLod = Math.Max(0, value);
                    _regenerateSampler = true;
                }
            }
        }

        public VkImageType ImageType => TextureExtensions.GetImageTypeFromViewType(_imageImageViewType);

        public VkImageLayout ImageLayout
        {
            get => _imageLayout;
        }

        public VkFormat Format => _imageFormat;

        public int BufferInstanceSize => Vulkan.BlockSize(_imageFormat);
        public ulong BufferInstanceCount => _vkBufferSizeRequirement / (uint)BufferInstanceSize;

        public virtual VkDescriptorImageInfo ImageInfo => _imageInfo;

        public VkExtent3D ImageExtent => _imageExtent;

        public int Height => (int)_imageExtent.height;
        public int Width => (int)_imageExtent.width;
        public int Depth => (int)_imageExtent.depth;

        internal virtual void UpdateDescriptor()
        {
            _imageInfo = new()
            {
                imageLayout = _imageLayout,
                imageView = _imageView,
                sampler = TextureSampler
            };
        }

        public unsafe virtual VkImageCreateInfo GetImageCreateInfo()
        {
            VkImageCreateInfo createInfo = new()
            {
                imageType = ImageType,
                extent = _imageExtent,
                mipLevels = _mipMapCount,
                arrayLayers = 1,
                format = _imageFormat,
                tiling = _imageTiling,
                initialLayout = VkImageLayout.Undefined,
                usage = _useageFlags,
                samples = _sampleCountFlags,
                sharingMode = _sharingMode,
            };

            if(_sharingMode == VkSharingMode.Concurrent)
            {
                Debug.Assert(_queueFamilyIndices != null, "Need to supply queue familyIndices!");
                Debug.Assert(_queueFamilyIndices.Length > 1, "Need to supply queue familyIndices!");

                createInfo.queueFamilyIndexCount = (uint)_queueFamilyIndices.Length;
                fixed (uint* pFamilyIndices = &_queueFamilyIndices[0])
                {
                    createInfo.pQueueFamilyIndices = pFamilyIndices;
                    return createInfo;
                }
            }

            return createInfo;
        }

        public virtual VkImageViewCreateInfo GetImageViewCreateInfo()
        {
            Debug.Assert(_vkImage != VkImage.Null, "Cannot create image view before underlying VkImage!");
            return new()
            {
                flags = _imageViewCreateFlags,
                image = _vkImage,
                viewType = _imageImageViewType,
                format = _imageFormat,
                components = _swizzle,
                subresourceRange = GetSubresourceRange()
            };
        }

        public virtual VkImageSubresourceRange GetSubresourceRange()
        {
            return new(_aspectFlags, _baseMipMapLevel, _mipMapCount, 0, 1);
        }

        public virtual VkSamplerCreateInfo GetSamplerCreateInfo()
        {
            return _samplerCreateInfo;
        }

        public unsafe int GetSamplerId()
        {
            var createInfo = _samplerCreateInfo;
            return ShaderProperties.Hash((byte*)&createInfo, (uint)sizeof(VkSamplerCreateInfo));
        }


        public void RegenerateMipMapsNow()
        {
            var cmd = GraphicsDevice.BeginSingleTimeMainPipe();
            RegenerateMipMaps(cmd);
            GraphicsDevice.EndSingleTimeMainPipe(cmd);
        }

        public abstract void RegenerateMipMaps(VkCommandBuffer cmd);

        public void SetImageLayout(VkImageLayout newImageLayout, VkPipelineStageFlags2 srcStage = VkPipelineStageFlags2.FragmentShader, VkPipelineStageFlags2 dstStage = VkPipelineStageFlags2.FragmentShader)
        {
            if (newImageLayout == ImageLayout) return;
            TextureExtensions.SetImageLayout(this, newImageLayout, srcStage, dstStage);
        }

        public void SetImageLayout(VkCommandBuffer cmdbuffer, VkImageLayout newImageLayout, VkPipelineStageFlags2 srcStage = VkPipelineStageFlags2.FragmentShader, VkPipelineStageFlags2 dstStage = VkPipelineStageFlags2.FragmentShader)
        {
            SetImageLayout(cmdbuffer, newImageLayout, GetSubresourceRange(), srcStage, dstStage);
        }

        public void SetImageLayoutAuto(VkCommandBuffer cmdBuffer, VkImageLayout newLayout)
        {
            var dstStage = newLayout.GetStageFlagFromLayout();
            var srcStage = _imageLayout.GetStageFlagFromLayout();
            SetImageLayout(cmdBuffer,newLayout,srcStage, dstStage);
        }

        public VkImageMemoryBarrier2 GetImageLayoutBarrierAuto(VkImageLayout newLayout)
        {
            var dstStage = newLayout.GetStageFlagFromLayout();
            var srcStage = _imageLayout.GetStageFlagFromLayout();
            return GetImageLayoutBarrier(newLayout, GetSubresourceRange(), srcStage, dstStage);
        }

        public unsafe virtual VkImageMemoryBarrier2 GetImageLayoutBarrier(VkImageLayout newImageLayout, VkImageSubresourceRange resourceRange, VkPipelineStageFlags2 srcStage = VkPipelineStageFlags2.FragmentShader, VkPipelineStageFlags2 dstStage = VkPipelineStageFlags2.FragmentShader)
        {
            return MemoryBarrierHelper.GetImageLayoutBarrier(_vkImage, _imageLayout, newImageLayout, resourceRange, srcStage, dstStage);
        }

        public unsafe virtual void SetImageLayout(VkCommandBuffer cmdbuffer, VkImageLayout newImageLayout, VkImageSubresourceRange resourceRange, VkPipelineStageFlags2 srcStage = VkPipelineStageFlags2.FragmentShader, VkPipelineStageFlags2 dstStage = VkPipelineStageFlags2.FragmentShader)
        {
            if (newImageLayout == _imageLayout)
            {
                return;
            }
            var barrier = GetImageLayoutBarrier(newImageLayout, resourceRange, srcStage, dstStage);
            MemoryBarrierHelper.ImageMemoryBarrier(cmdbuffer, &barrier, 1);
            SetImageLayoutSilent(newImageLayout);
        }

        public void SetImageLayoutSilent(VkImageLayout newImageLayout)
        {
            _imageLayout = newImageLayout;
            UpdateDescriptor();
        }

        public virtual void Apply()
        {
            if (_hostBuffer != null)
            {
                this.CopyFromBuffer(_hostBuffer);
            }
        }

        public virtual int MipStartOffset(int mipLevel)
        {
            if (mipLevel == 0) return 0;
            var length = GetMipLength(mipLevel);
            return length * BufferInstanceSize;
        }

        public virtual int GetMipLength(int mipLevel)
        {
            var resolution = GetMipResolution(mipLevel);
            return (int)resolution.width * (int)resolution.height;
        }

        public virtual VkExtent3D GetMipResolution(int mipLevel)
        {
            if (mipLevel == 0) return _imageExtent;
            return new VkExtent3D(
                (int)(_imageExtent.width >> (int)mipLevel),
                (int)(_imageExtent.height >> (int)mipLevel),
                1
            );
        }

        public override void Dispose()
        {

            if (_disposed)
            {
                return;
            }
            GC.SuppressFinalize(this);

            _hostBuffer?.EnqueueForDisposal();

            TextureExtensions.EnqueueForDisposal(_vkImage, _allocation, _imageView, VkSampler.Null);

            _disposed = true;
            GC.ReRegisterForFinalize(this);
        }

        protected virtual void Reinitialise()
        {
            TextureExtensions.EnqueueForDisposal(_vkImage, _allocation, _imageView, VkSampler.Null);
            if(MipMapCount > 1)
            {
                MipMapCount = TextureExtensions.CalculateMipMapLevels(Width, Height);
            }
            _imageLayout = VkImageLayout.Undefined;
            _vkImage = VkImage.Null;
            _allocation = VmaAllocation.Null;
            _imageView = VkImageView.Null;
            this.CreateImage(GetImageCreateInfo());
            this.CreateImageView(GetImageViewCreateInfo());

            UpdateDescriptor();
        }

        public void Reinitialise(VkComponentMapping mapping)
        {
            _swizzle = mapping;
            TextureExtensions.EnqueueForDisposal(VkImage.Null, VmaAllocation.Null, _imageView, VkSampler.Null);
            _imageLayout = VkImageLayout.Undefined;
            _imageView = VkImageView.Null;
            this.CreateImageView(GetImageViewCreateInfo());

            UpdateDescriptor();
        }

        public abstract void Reload();
    }
}