#define PARALLEL_SHADER_LOADING
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
#if PARALLEL_SHADER_LOADING
using System.Threading.Tasks;
#endif
using VECS.LowLevel;
using Vortice.SPIRV.Reflect;
using Vortice.Vulkan;


namespace VECS
{
    public class ShaderModule : DisposableAsset, IComparable<ShaderModule>
    {
        private readonly struct DisposeShader : IDisposable
        {
            private readonly VkShaderModule _vkShaderModule;
            private readonly SpvReflectShaderModule _spvShaderModule;

            public DisposeShader(ShaderModule module)
            {
                _vkShaderModule = module._vkShaderModule;
                _spvShaderModule = module._spvShaderModule;
                module._vkShaderModule = VkShaderModule.Null;
                module._spvShaderModule = default;
            }

            public readonly void Dispose()
            {
                SPIRVReflectUtil.DestroyReflectShaderModule(_spvShaderModule);
                GraphicsDevice.DeviceAPI.vkDestroyShaderModule(_vkShaderModule);
            }
        }

        private static readonly ConcurrentQueue< DisposeShader> _shaderModuleDisposalQueue = new();

        private static readonly List<(ulong, DisposeShader)> _shaderDisposalList = [];

        public static string PreCompiledShaders => Path.Combine(ShaderCompiler.ShaderFilePath, "PreCompiled");


        private readonly ConcurrentBag<string> _registedPipelines = [];

        private readonly ConcurrentBag<int> _registedLayouts = [];

        private VkShaderModule _vkShaderModule;
        private SpvReflectShaderModule _spvShaderModule;
        private VkShaderStageFlags _vkStage = VkShaderStageFlags.None;
        private SpvReflectShaderStageFlags _spvStage = SpvReflectShaderStageFlags.None;
        private Vector3UInt _computeShaderLocalSize = new(1, 1, 1);

        private VkVertexInputBindingDescription[] _vertexBindings = [];
        private VkVertexInputAttributeDescription[] _vertexAttributes = [];
        private bool _hasVertexAttributes= false;

        private DescriptorBinding[] _descriptorBindings;

        public VkShaderModule VkShaderModule => _vkShaderModule;
        public SpvReflectShaderModule SpvShaderModule => _spvShaderModule;

        public VkShaderStageFlags VkShaderStage => _vkStage;
        public SpvReflectShaderStageFlags SpvShaderStage => _spvStage;
        public Vector3UInt ComputeShaderLocalSize => _computeShaderLocalSize;
        public bool HasVertexAttributes => _hasVertexAttributes;
        public VkVertexInputBindingDescription[] VertexBindings =>_vertexBindings;
        public VkVertexInputAttributeDescription[] VertexAttributes => _vertexAttributes;

        public DescriptorBinding[] DescriptorBindings => _descriptorBindings;

        public VkPipelineShaderStageCreateInfo ShaderStageCreateInfo
        {
            get
            {
                VkUtf8ReadOnlyString entryPoint = Encoding.UTF8.GetBytes(_spvShaderModule.EntryPointName);
                return new()
                {
                    stage = _vkStage,
                    module = _vkShaderModule,
                    pName = entryPoint
                };
            }
        }

        internal ShaderModule(string filePath)
        {
            FileName = Path.GetFileName(filePath);
            AssetName = Path.GetFileNameWithoutExtension(filePath);

            byte[] shaderCode = File.ReadAllBytes(filePath);
            InternalCreate(shaderCode);
        }

        internal ShaderModule(string name, byte[] shaderCode)
        {
            AssetName = name;
            InternalCreate(shaderCode);
        }

        private unsafe void InternalCreate(byte[] shaderCode)
        {
            if (shaderCode == null)
            {
                throw new NullReferenceException(string.Format("Shader Code was null: {0}", AssetName));
            }

            _spvShaderModule = SPIRVReflectUtil.CreateReflectShaderModule(shaderCode);

            _spvStage = _spvShaderModule.shader_stage;
            _vkStage = (VkShaderStageFlags)_spvStage;
            bool disposeNow = false;
            if (!GraphicsDevice.MeshShading)
            {
                switch (_vkStage)
                {
                    case VkShaderStageFlags.MeshEXT:
                        disposeNow = true;
                        break;
                    case VkShaderStageFlags.TaskEXT:
                        disposeNow = true;
                        break;
                }
            }
            if(_vkStage == VkShaderStageFlags.Compute)
            {
                _computeShaderLocalSize = new(_spvShaderModule.entry_points[0].local_size.x, _spvShaderModule.entry_points[0].local_size.y, _spvShaderModule.entry_points[0].local_size.z);
            }
            if (disposeNow)
            {
                _disposed = true;
                SPIRVReflectUtil.DestroyReflectShaderModule(_spvShaderModule);
                return;
            }

            GraphicsDevice.DeviceAPI.vkCreateShaderModule(shaderCode, null, out _vkShaderModule).CheckResult("Failed to Create Shader Module!");
            GraphicsDevice.SetObjectName(VkObjectType.ShaderModule, _vkShaderModule.Handle, AssetName);
            if (_vkStage.HasFlag(VkShaderStageFlags.Vertex) && GPUPipelineUtil.GetVertexInputState(_spvShaderModule, out _vertexBindings, out _vertexAttributes))
            {
                _hasVertexAttributes = true;
            }
            _descriptorBindings = GPUPipelineUtil.GenerateDescriptorBindings(_spvShaderModule);
        }

        internal void ReplaceShader(byte[] shaderCode)
        {
            _shaderModuleDisposalQueue.Enqueue(new(this));
            InternalCreate(shaderCode);

            HashSet<string> pipelines = [];

            foreach (var item in _registedPipelines)
            {
                if (pipelines.Contains(item))
                {
                    continue;
                }
                pipelines.Add(item);
                var graphicsPipeline = AssetDataBase<GraphicsPipeline>.GetNamedSilentFail(item);

                if (graphicsPipeline != null)
                {
                    PipelineRecreation.EnqueueShaderChanged(graphicsPipeline);
                    continue;
                }

                var computePipeline = AssetDataBase<ComputePipeline>.GetNamedSilentFail(item);

                if(computePipeline!= null)
                {
                    PipelineRecreation.EnqueueShaderChanged(computePipeline);
                }
            }

            foreach(var item in _registedLayouts)
            {
                ShaderPipelineLayout.EnqueueForDisposal(AssetDataBase<ShaderPipelineLayout>.GetHashed(item));
            }
        }

        internal void RegisterLayout(ShaderPipelineLayout layout)
        {
            _registedLayouts.Add(layout.Hash);
        }

        internal void RegisterGraphicsPipeline(GraphicsPipeline pipeline)
        {
            _registedPipelines.Add(pipeline.AssetName);
        }

        internal void RegisterComputePipeline(ComputePipeline pipeline)
        {
            _registedPipelines.Add(pipeline.AssetName);
        }

        public override void Dispose()
        {
            GC.SuppressFinalize(this);
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shaderModuleDisposalQueue.Enqueue(new(this));
            GC.ReRegisterForFinalize(this);
        }

        public static void PlayBackDisposalCmds()
        {
            while(_shaderModuleDisposalQueue.TryDequeue(out var module))
            {
                _shaderDisposalList.Add((Presenter.FrameCount + SwapChain.MAX_CONCURRENT_FRAMES_UINT + 1, module));
            }

            for (int i = _shaderDisposalList.Count - 1; i >= 0; i--)
            {
                if (_shaderDisposalList[i].Item1 > Presenter.FrameCount)
                {
                    _shaderDisposalList[i].Item2.Dispose();
                    _shaderDisposalList.RemoveAt(i);
                    continue;
                }
                var tuple = _shaderDisposalList[i];
                tuple.Item1++;
                _shaderDisposalList[i] = tuple;
            }
        }

        public static void CleanUp()
        {
            PlayBackDisposalCmds();
            _shaderDisposalList.ForEach(shader=>shader.Item2.Dispose());
        }

        public static ShaderModule Create(string filePath)
        {
            var module = new ShaderModule(filePath);
            if (module.IsDisposed)
            {
                return null;
            }
            AssetDataBase<ShaderModule>.Add(module);

            return module;
        }

        public static ShaderModule CreateNoAdd(string name, byte[] shaderBytes)
        {
            var module = new ShaderModule(name, shaderBytes);
            if (module.IsDisposed)
            {
                return null;
            }

            AssetDataBase<ShaderModule>.Add(module);

            return module;
        }

        public static void LoadAllShaders()
        {
            ShaderCompiler.LoadAllShaders();
            Console.WriteLine("Loading Pre Compiled Shader Files..");
            Stopwatch stopwatch = new();
            stopwatch.Start();
            var dir = new DirectoryInfo(PreCompiledShaders);
            var shaderFiles = dir.GetFiles("*.spv", SearchOption.AllDirectories);
            ShaderModule[] shaderModules = new ShaderModule[shaderFiles.Length];
#if PARALLEL_SHADER_LOADING
            Parallel.ForEach(shaderFiles, (shaderFile, state, index) =>
            {
                shaderModules[(int)index] = new ShaderModule(shaderFile.FullName)
                {
                    Generated = true
                };
            });
#else
            for (int i = 0; i < shaderFiles.Length; i++)
			{
                shaderModules[i] = new ShaderModule(shaderFiles[i].FullName)
                {
                    Generated = true
                };
			}
#endif
            for (int i = 0; i < shaderModules.Length; i++)
            {
                if (shaderModules[i].IsDisposed) continue;
                AssetDataBase<ShaderModule>.Add(shaderModules[i]);
            }
            stopwatch.Stop();
            Console.WriteLine("{0} Shader files loaded in {1}ms", AssetDataBase<ShaderModule>.AssetCount, stopwatch.ElapsedMilliseconds);
        }

        internal static void ReloadPreCompiledShader(string name, string path)
        {
            throw new NotImplementedException();
            var existing = AssetDataBase<ShaderModule>.GetNamedSilentFail(Path.GetFileNameWithoutExtension(name));

            byte[] shaderCode = File.ReadAllBytes(path);

            existing.ReplaceShader(shaderCode);
        }

        public int CompareTo(ShaderModule other)
        {
            if(VkShaderStage != VkShaderStageFlags.Fragment   && other.VkShaderStage == VkShaderStageFlags.Fragment)
            {
                return -1;
            }
            if(VkShaderStage == VkShaderStageFlags.Vertex && other.VkShaderStage == VkShaderStageFlags.Geometry)
            {
                return -1;
            }
            if(VkShaderStage == VkShaderStageFlags.MeshEXT && other.VkShaderStage == VkShaderStageFlags.TaskEXT)
            {
                return 1;
            }
            return 0;
        }
    }
}