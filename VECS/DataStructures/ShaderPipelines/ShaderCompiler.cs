using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VECS.LowLevel;
using Vortice.ShaderCompiler;

namespace VECS
{
    public class ShaderCompiler
    {
        private static readonly ConcurrentQueue<string> _recompileQueue = [];

        public static string ShaderFilePath => Path.Combine(Asset.AssetsPath, "Shaders");
        public static readonly string[] _compileTags = [".frag", ".vert", ".comp", ".mesh", ".task"];

        private static ShaderKind GetShaderKindFromFileExtention(string filePath)
        {
            var extention = Path.GetExtension(filePath);
            extention = extention.ToLower();
            
            return extention switch
            {
                
                ".frag" => ShaderKind.GLSL_FragmentShader,
                ".vert" => ShaderKind.GLSL_VertexShader,
                ".comp" => ShaderKind.GLSL_ComputeShader,
                ".mesh" => ShaderKind.GLSL_MeshShader,
                ".task" => ShaderKind.GLSL_TaskShader,
                _=> throw new NotSupportedException()
            };
        }

        private static SourceLanguage GetShadingLanguage(string filePath)
        {
            if (filePath.Contains("hlsl", StringComparison.CurrentCultureIgnoreCase))
            {
                return SourceLanguage.HLSL;
            }
            return SourceLanguage.GLSL;
        }

        public static void LoadAllShaders()
        {
            Console.WriteLine("Loading Un-Compiled Shader Files..");
            Stopwatch stopwatch = new();
            stopwatch.Start();
            var dir = new DirectoryInfo(ShaderFilePath);
            var shaderFiles = new List<FileInfo>(dir.GetFiles("",SearchOption.AllDirectories));

            for (int i = shaderFiles.Count - 1; i >= 0; i--)
            {
                if (!_compileTags.Contains(shaderFiles[i].Extension))
                {
                    shaderFiles.RemoveAt(i);
                }
            }
            
            ShaderModule[] shaderModules = new ShaderModule[shaderFiles.Count];

            Application.ParallelFor(shaderModules.Length, (i) =>
            {
                shaderModules[i] = Compile(shaderFiles[i].FullName);
            });

            for (int i = 0; i < shaderModules.Length; i++)
            {
                if (shaderModules[i] == null || shaderModules[i].IsDisposed) continue;
                AssetDataBase<ShaderModule>.Add(shaderModules[i]);
            }
            Console.WriteLine("{0} Shader files loaded & Compiled in {1}ms", AssetDataBase<ShaderModule>.AssetCount, stopwatch.ElapsedMilliseconds);
        }

        public static ShaderModule Compile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var shaderCompiler = new Compiler();
            CompilerOptions options = new()
            {
                TargetEnv = TargetEnvironmentVersion.Vulkan_1_3,
                NaNClamp = true,
                SourceLanguage = GetShadingLanguage(filePath),
                TargetSpv = SpirVVersion.Version_1_6,
#if DEBUG
                OptimizationLevel = OptimizationLevel.Zero,
                GeneratedDebug = true,
#else
                OptimizationLevel = OptimizationLevel.Performance,
                GeneratedDebug= true,
#endif

                ShaderStage = GetShaderKindFromFileExtention(filePath)
            };

            if (!GraphicsDevice.MeshShading && (options.ShaderStage == ShaderKind.GLSL_MeshShader || options.ShaderStage == ShaderKind.GLSL_TaskShader))
            {
                return null;
            }
            CompileResult compileResult = null;
            try
            {
                 compileResult = shaderCompiler.Compile(filePath, options);
            }
            catch (IOException)
            {
                _recompileQueue.Enqueue(filePath);
                return null;
            }
            switch (compileResult.Status)
            {
                case CompilationStatus.Success:
                    if (compileResult.WarningsCount > 0)
                    {
                        Console.WriteLine("Compiled shader \"{0}\" with {2} warnings \n{1}", Path.GetFileName(filePath), compileResult.ErrorMessage, compileResult.WarningsCount);
                    }

                    var fileName = Path.GetFileName(filePath);

                    var existingModule = AssetDataBase<ShaderModule>.GetNamedSilentFail(fileName);

                    if (existingModule == null)
                    {
                        ShaderModuleMetaFile metaFile;
                        
                        if (AssetMetaFile.MetaFileExists(filePath))
                        {
                            metaFile = AssetMetaFile.TryLoad<ShaderModuleMetaFile>(filePath);
                        }
                        else
                        {
                            metaFile = new(filePath,null);
                            metaFile.SaveMetaFile();
                        }
                        
                        ShaderModule  module = new(fileName, compileResult.Bytecode)
                        {
                            MetaFile = metaFile
                        };
                        
                        metaFile.TargetInstance = module;
                        return module;
                    }
                    else
                    {
                        existingModule.ReplaceShader(compileResult.Bytecode);
                    }

                    break;
                default:
                    Console.WriteLine("Failed to compile shader \"{0}\" with {2} errors and {3} warnings \n{1}",Path.GetFileName(filePath),compileResult.ErrorMessage,compileResult.ErrorsCount,compileResult.WarningsCount);
                    Debugger.Break();
                    break;
            }

            shaderCompiler.Dispose();
            return null;
        }

        internal static void Recompile(string name, string path)
        {
            if(Path.GetExtension(path) == ".glsl")
            {
                return;
            }
            _recompileQueue.Enqueue(path);
            
        }

        internal static void PlaybackRecompileCmds()
        {
            if (_recompileQueue.IsEmpty) return;

            HashSet<string> paths = new (_recompileQueue.Count);

            while(_recompileQueue.TryDequeue( out var path))
            {
                paths.Add(path);
            }

            foreach (var item in paths)
            {
                Compile(item);
            }
        }
    }
}
