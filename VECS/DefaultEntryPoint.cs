using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using VECS.ECS;
using VECS.ECS.Presentation;
using VECS.ECS.Transforms;
using Vortice.Vulkan;

namespace VECS
{
    internal static class DefaultEntryPoint
    {
        public static readonly string ProjectName = "Sponza-Renderer-Testing";
        private static Vector3 initalCameraPos = new(-0.12f, 1.14f, -2.25f);
        private static Vector3 initalCameraRot = TransformExtensions.DegreesToRadians(new (17.0f, 7.0f, 0.0f));// TransformExtensions.DegreesToRadians(new(0, 90, 0));

        private static DirectSubMesh _sphere;

        private static CameraPerspective cameraPerspective = new()
        {
            FOV = 45,
            ClipNear = 0.5f,
            ClipFar = 50f,
            CullMode = CullModeFlags.Fustrum | CullModeFlags.Distance
        };

        internal static int Main(string[] args)
        {
            try
            {
                Application app = new();
                app.PreOnCreate += PreCreate;
                app.Run();
                app.PreOnCreate -= PreCreate;
                app.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("{0},\n{1}", ex.Message, ex.StackTrace));
                if (ex.InnerException != null)
                {
                    Console.WriteLine(string.Format("{0},\n{1}", ex.InnerException.ToString(), ex.InnerException.StackTrace));
                }
                Console.ReadLine();
                return 1;
            }
            return 0;
        }

        private static void PreCreate()
        {
            _sphere = MeshLoader.LoadModelFromFile(MeshLoader.GetMeshInDefaultPath("UV-Sphere.obj"), null)[0];
            CreateMainCamera();
            DirectionalLight();
            PointLight();
            //SponzaOld();
            //SponzaNew();
            SponzaNewPBR();
            //ShadowDebug();
        }

        private static void CreateMainCamera()
        {
            EntityManager entityManager = World.DefaultWorld.EntityManager;
            Entity MainCamera = entityManager.CreateEntity("Main Camera");
            entityManager.AddComponent<FreeCamera>(MainCamera, new() { AngleX = TransformExtensions.Rad2Deg * initalCameraRot.X, AngleY = TransformExtensions.Rad2Deg * initalCameraRot.Y });
            entityManager.AddComponent(MainCamera, new Translation() { Value = initalCameraPos });
            entityManager.AddComponent<Rotation>(MainCamera, new() { Value = NumericsExtensions.CameraRotation(TransformExtensions.Rad2Deg * initalCameraRot.X,TransformExtensions.Rad2Deg * initalCameraRot.Y) });
            entityManager.AddComponent(MainCamera, cameraPerspective);
            entityManager.AddComponent<MainCamera>(MainCamera);
            return;
            MainCamera = entityManager.CreateEntity("Spot Light");
            entityManager.AddComponent(MainCamera, new Translation() { Value = new Vector3(0,1,0) });
            entityManager.AddComponent(MainCamera, new Rotation() { Value = TransformExtensions.Euler(initalCameraRot) });
            entityManager.AddComponent(MainCamera, new SpotLight()
            {
                Ambient = new(0.1f, 0.1f, 0.1f, 1f),
                Diffuse = new(0, 1, 0, 1),
                Specular = new(0.5f,4f, 0.5f, 1f),

                Constant = 1.0f,
                Linear = 0.07f,
                Quadratic = 0.017f,

                cutOff = MathF.Cos(TransformExtensions.Deg2Rad * 30.5f),
                outerCutOff = MathF.Cos(TransformExtensions.Deg2Rad * 37.5f),
                range = 25
            });
            entityManager.AddComponent(MainCamera, new Scale() { Value = new Vector3(0.25f, 0.25f, 0.25f) });

            entityManager.AddComponent<MainColour>(MainCamera, new() { Value = new(0, 1, 0, 1) });
            entityManager.AddComponent(MainCamera, new ShadowInfo()
            {
                UpdateBehaviour = ShadowUpdate.Always,
                Resolution = ShadowMapResolution.TwentyFourtyEight.GetResolution(),
            });
            AddRenderMeshComponents(MainCamera, EnginePipes.Unlit.Default(), 0, _sphere, entityManager, RenderLayer.Default | RenderLayer.NoShadow);

            //var secondCamera = entityManager.CreateEntity();
            //entityManager.AddComponent(secondCamera, new LocalToWorld() { Value = TransformExtensions.TRS(initalCameraPos, initalCameraRot, Vector3.One) });
            //entityManager.AddComponent(secondCamera, cameraPerspective);
        }

        private static void DirectionalLight()
        {
            EntityManager entityManager = World.DefaultWorld.EntityManager;
            var dirLight = entityManager.CreateEntity("Directional Light");

            entityManager.AddComponent(dirLight, new DirectionalLight()
            {
                Value = new()
                {
                    Direction = new Vector4(Vector3.Normalize(new(-0.5f,-1, 0.25f)),0),
                    // Direction = new Vector4(0,-1,0,0),

                    Ambient = new(0.1f, 0.1f, 0.1f, 1f),
                    Diffuse = Vector4.One,
                    Specular = new(4f, 4f, 4f, 1f)
                }
            });

            entityManager.AddComponent(dirLight, new ShadowInfo()
            {
                UpdateBehaviour = ShadowUpdate.Always,
                Resolution = ShadowMapResolution.TwentyFourtyEight.GetResolution(),
            });
        }

        public static void PointLight()
        {
            EntityManager entityManager = World.DefaultWorld.EntityManager;
            
            
            PointLight(entityManager, new(-10, 1, 0), new(4, 0, 0, 1), _sphere);
            // PointLight(entityManager, new(10, 1f, 0), new(1, 0, 0, 1), _sphere);
            // 
            // PointLight(entityManager, new(8, 1, 0), new(0, 1, 0, 1), _sphere);
            // PointLight(entityManager, new(-8, 1, 0), new(0, 1, 0, 1), _sphere);
            // 
            // PointLight(entityManager, new(6, 1, 0), new(0, 0, 1, 1), _sphere);
            // PointLight(entityManager, new(-6, 1, 0), new(0, 0, 1, 1), _sphere);
            // 
            // PointLight(entityManager, new(4, 1, 0), new(1, 1, 0, 1), _sphere);
            // PointLight(entityManager, new(-4, 1, 0), new(1, 1, 0, 1), _sphere);
            // 
            // PointLight(entityManager, new(2, 1, 0), new(0, 1, 1, 1), _sphere);
            // PointLight(entityManager, new(-2, 1, 0), new(0, 1, 1, 1), _sphere);

        }

        private static void PointLight(EntityManager entityManager,Vector3 translation, Vector4 diffuse, DirectSubMesh subMesh)
        {
            var pointLight = entityManager.CreateEntity("Point Light");

            entityManager.AddComponent(pointLight, new Translation() { Value = translation });

            entityManager.AddComponent(pointLight, new PointLight()
            {
                Ambient = new(0.1f, 0.1f, 0.1f, 1f),
                Diffuse = diffuse,
                Specular = diffuse,

                Constant = 1.0f,
                Linear = 0.07f,
                Quadratic = 0.017f,
                Range = 7f
            });

            entityManager.AddComponent(pointLight, new ShadowInfo()
            {
                UpdateBehaviour = ShadowUpdate.Always,
                Resolution = 1365,
            });

            entityManager.AddComponent(pointLight, new Scale() { Value = new Vector3(0.5f, 0.5f, 0.5f) });

            entityManager.AddComponent<MainColour>(pointLight, new() { Value = diffuse*20f });

            AddRenderMeshComponents(pointLight,EnginePipes.Unlit.Default(), 0, subMesh, entityManager,RenderLayer.Default | RenderLayer.NoShadow);
            MaterialProvider material = new("PointLightUnlit", EnginePipes.DepthOnly.Create("Unlit"), EnginePipes.Unlit.Default());
            AssetDataBase<MaterialProvider>.Add(material);
            entityManager.AddComponent(pointLight, new MaterialProviderComponent() { Value = material.Hash, LayerFlags = RenderLayer.Default | RenderLayer.NoShadow });
        }

        private static void ShadowDebug()
        {
            EntityManager entityManager = World.DefaultWorld.EntityManager;

            var mesh = MeshLoader.LoadModelFromFile(MeshLoader.GetMeshInDefaultPath("quad.obj"),null)[0];

            var litTransparent = EnginePipes.Unlit_Tex_Deferred.Default();


            // var arraydef =new TextureDefintion("TestArray", Path.Combine(Asset.AssetsPath, "Textures", "Skyboxes", "GL_Skybox", "front.jpg"), Path.Combine(Asset.AssetsPath, "Textures", "Skyboxes", "GL_Skybox", "back.jpg"));
            // arraydef.FullFileName = Path.Combine(Asset.AssetsPath, "Textures", "ArrayTexture.TexDef");
            // arraydef.SaveJson();
            // arraydef = TextureDefintion.Load(arraydef.FullFileName);
            // var array = (Texture2DArray)arraydef.LoadTexture();

            var cubearray = new TextureDefintion("TestCubeArray",
                [
                    Path.Combine(Asset.AssetsPath, "Textures", "Skyboxes", "GL_Skybox", "front.jpg"),
                    Path.Combine(Asset.AssetsPath, "Textures", "Skyboxes", "GL_Skybox", "front.jpg"),
                ],
                [
                    Path.Combine(Asset.AssetsPath, "Textures", "Skyboxes", "GL_Skybox", "back.jpg"),
                    Path.Combine(Asset.AssetsPath, "Textures", "Skyboxes", "GL_Skybox", "back.jpg"),
                ],
                [
                    Path.Combine(Asset.AssetsPath, "Textures", "Skyboxes", "GL_Skybox", "left.jpg"),
                    Path.Combine(Asset.AssetsPath, "Textures", "Skyboxes", "GL_Skybox", "left.jpg"),
                ],
                [
                    Path.Combine(Asset.AssetsPath, "Textures", "Skyboxes", "GL_Skybox", "right.jpg"),
                    Path.Combine(Asset.AssetsPath, "Textures", "Skyboxes", "GL_Skybox", "right.jpg"),
                ],
                [
                    Path.Combine(Asset.AssetsPath, "Textures", "Skyboxes", "GL_Skybox", "top.jpg"),
                    Path.Combine(Asset.AssetsPath, "Textures", "Skyboxes", "GL_Skybox", "top.jpg"),
                ],
                [
                    Path.Combine(Asset.AssetsPath, "Textures", "Skyboxes", "GL_Skybox", "bottom.jpg"),
                    Path.Combine(Asset.AssetsPath, "Textures", "Skyboxes", "GL_Skybox", "bottom.jpg"),
                ]
            )
            {
                FullFileName = Path.Combine(Asset.AssetsPath, "Textures", "TestCubeArray.TexDef")
            };
            cubearray.SaveJson();
            cubearray = TextureDefintion.Load(cubearray.FullFileName);
            var cube = (CubemapArray)cubearray.LoadTexture();
            litTransparent.SetTexture("texSampler".GetShaderPropertyId(), cube);
            var entity = entityManager.CreateEntity();
            AddRenderMeshComponents(entity, litTransparent, 0, mesh, entityManager);


            entityManager.AddComponent(entity, new Translation() { Value = new Vector3(0, 2, 1) });
            entityManager.AddComponent<MainColour>(entity, new() { Value = new Vector4(1,1,1,0.5f) });

            entityManager.AddComponent(entity, new Rotation() { Value = TransformExtensions.EulerUnity(0, 0, -180) });
        }

        private static void SponzaNew()
        {
            MeshLoader.LoadModelFromFile(MeshLoader.GetMeshInDefaultPath("Sponza-New.obj"), [new VertexAttributeDescription(VertexAttribute.Tangent, VertexAttributeFormat.Float4)], out var sponza, out var sponzaMatInfo);

            EntityManager entityManager = World.DefaultWorld.EntityManager;

            Dictionary<string, Texture2D> textureLibrary = [];

            var commonParent = entityManager.CreateEntity("Sponza-New");


            Children children = new()
            {
                Values = new Entity[sponza.Length]
            };
            Parent parent = new() { Value = commonParent };

            var lit = EnginePipes.LitTexture;
            //var litTransparent = EnginePipes.OIT_LitTexture;

            var texProp = "texSampler".GetShaderPropertyId();
            var normalProp = "normalSampler".GetShaderPropertyId();
            var texColour = "texProps.colour".GetShaderPropertyId();
            var texSpecColour = "texProps.specularColour".GetShaderPropertyId();
            var texTiling = "texProps.tiling".GetShaderPropertyId();
            var shininess = "texProps.shininess".GetShaderPropertyId();

            int litVariant = 0;

            for (int i = 0, k = 0; i < sponzaMatInfo.Length; i++)
            {
                var matInfo = sponzaMatInfo[i];

                string matName = "sponza_new_" + matInfo.Name;
                if (string.IsNullOrEmpty(matName))
                {
                    matName = "sponza_new_Mat_" + i;
                }

                Material material = AssetDataBase<Material>.GetNamedSilentFail(matName);
                material ??= lit.Create(matName);

                if (matInfo.DiffuseTexture != null)
                {
                    if (!textureLibrary.TryGetValue(matInfo.DiffuseTexture, out var diffuseTexture))
                    {
                        diffuseTexture = TextureLoader.Load2D(matInfo.DiffuseTexture,VkFormat.Bc7UnormBlock); //new Texture2D(matInfo.DiffuseTexture);
                        textureLibrary.Add(matInfo.DiffuseTexture, diffuseTexture);
                    }
                    if (matInfo.AlphaClipping)
                    {
                        //material.SetTexture(ShaderProperties.HeadIndexImageId, Presenter.Instance.ForwardRenderer._headIndex);
                        material.AlphaClipping = true;
                        material.OverrideCullMode = true;
                        material.CullMode = Vortice.Vulkan.VkCullModeFlags.None;
                        material.AlphaTexture = diffuseTexture;
                    }
                    material.SetTexture(texProp, diffuseTexture);
                }
                else
                {
                    material.SetTexture(texProp, EngineTextures.White);
                }
                if (matInfo.NormalTexture != null)
                {
                    if (!textureLibrary.TryGetValue(matInfo.NormalTexture, out var normalTexture))
                    {
                        normalTexture = TextureLoader.Load2D(matInfo.NormalTexture, VkFormat.Bc5UnormBlock); // new Texture2D(matInfo.NormalTexture, true,false,false);
                        //normalTexture.Reinitialise(new VkComponentMapping(VkComponentSwizzle.A, VkComponentSwizzle.G, VkComponentSwizzle.B, VkComponentSwizzle.R));

                        textureLibrary.Add(matInfo.NormalTexture, normalTexture);
                    }

                    material.SetTexture(normalProp, normalTexture);
                }
                else
                {
                    material.SetTexture(normalProp, EngineTextures.Black);
                }

                material.SetVector4(texColour, matInfo.DiffuseColour);
                material.SetVector4(texSpecColour, Vector4.Zero);
                material.SetFloat(texTiling, 1);

                material.SetFloat(shininess, 32);

                for (int j = 0; j < matInfo.appliesTo.Count; j++, k++)
                {
                    var meshIndex = matInfo.appliesTo[j];
                    var entity = entityManager.CreateEntity(string.Format("Sponza Component {0}",k));
                    children.Values[k] = entity;
                    entityManager.AddComponent(entity, parent);

                    AddRenderMeshComponents(entity, material, 0, sponza[meshIndex], entityManager);
                }
                litVariant++;
            }

            entityManager.AddComponent(commonParent, new Rotation() { Value = TransformExtensions.EulerUnity(00,90,0) });
            entityManager.AddComponent(commonParent, new Scale() { Value = Vector3.One });
            entityManager.AddComponent(commonParent, children);

        }

        private static void SponzaNewPBR()
        {

            Stopwatch sw = Stopwatch.StartNew();
            MeshLoader.LoadModelFromFile(MeshLoader.GetMeshInDefaultPath("Sponza-New.obj"), [new VertexAttributeDescription(VertexAttribute.Tangent, VertexAttributeFormat.Float4)], out var sponza, out var sponzaMatInfo);
            sw.Stop();
            Console.WriteLine("Sponza Mesh Import time: {0}ms", sw.ElapsedMilliseconds);

            Dictionary<string, Texture2D> textureLibrary = [];
            
            EntityManager entityManager = World.DefaultWorld.EntityManager;

            var commonParent = entityManager.CreateEntity();


            Children children = new()
            {
                Values = new Entity[sponza.Length]
            };
            Parent parent = new() { Value = commonParent };

            var lit = EnginePipes.PBRTexture;
            lit = EnginePipes.PBR_Deferred;
            //var litTransparent = EnginePipes.OIT_LitTexture;

            var texProp = "albedoMap".GetShaderPropertyId();
            var normalProp = "normalMap".GetShaderPropertyId();
            
            var maskProp = "maskMap".GetShaderPropertyId();

            var texColour = "texProps.colour".GetShaderPropertyId();
            var texTiling = "texProps.tiling".GetShaderPropertyId();

            var exposureProp = "texProps.exposure".GetShaderPropertyId();
            var gammaProp = "texProps.gamma".GetShaderPropertyId();

            int litVariant = 0;
            for (int i = 0, k = 0; i < sponzaMatInfo.Length; i++)
            {
                var matInfo = sponzaMatInfo[i];

                string matName = "sponza_new_" + matInfo.Name;
                if (string.IsNullOrEmpty(matName))
                {
                    matName = "sponza_new_Mat_" + i;
                }

                Material material = AssetDataBase<Material>.GetNamedSilentFail(matName);
                material ??= lit.Create(matName);

                MaterialProvider materialProvider = AssetDataBase<MaterialProvider>.GetNamedSilentFail(matName);

                if(materialProvider == null)
                {
                    materialProvider = new MaterialProvider(matName, material);
                    AssetDataBase<MaterialProvider>.Add(materialProvider);
                }
                

                if (matInfo.DiffuseTexture != null)
                {
                    if (!textureLibrary.TryGetValue(matInfo.DiffuseTexture, out var diffuseTexture))
                    {
                        diffuseTexture = TextureLoader.Load2D(matInfo.DiffuseTexture,VkFormat.Bc7UnormBlock);
                        textureLibrary.Add(matInfo.DiffuseTexture, diffuseTexture);
                        materialProvider.DepthOnly = EnginePipes.DepthOnly.Default();
                    }
                    if (matInfo.AlphaClipping)
                    {
                        //material.SetTexture(ShaderProperties.HeadIndexImageId, Presenter.Instance.ForwardRenderer._headIndex);
                        material.AlphaClipping = true;
                        material.OverrideCullMode = true;
                        material.CullMode = VkCullModeFlags.None;
                        material.AlphaTexture = diffuseTexture;
                        var alphaClippingDepthVariant = EnginePipes.DepthOnlyAlphaClipping.Create(matName);
                        DrawBlob.SetAlphaClipping(material,alphaClippingDepthVariant);
                        materialProvider.DepthOnly = alphaClippingDepthVariant;
                    }
                    material.SetTexture(texProp, diffuseTexture);
                }
                else
                {
                    material.SetTexture(texProp, EngineTextures.White);
                }

                if (matInfo.NormalTexture != null)
                { 
                    if (!textureLibrary.TryGetValue(matInfo.NormalTexture, out var normalTexture))
                    {
                        normalTexture = TextureLoader.Load2D(matInfo.NormalTexture, VkFormat.Bc5UnormBlock);
                        textureLibrary.Add(matInfo.NormalTexture, normalTexture);
                    }
                    material.SetTexture(normalProp, normalTexture);
                }
                else
                {
                    material.SetTexture(normalProp, EngineTextures.Black);
                }

                if(matInfo.MaskTexture != null)
                {
                    if (!textureLibrary.TryGetValue(matInfo.MaskTexture, out var maskTexture))
                    {
                        maskTexture = TextureLoader.Load2D(matInfo.MaskTexture, VkFormat.Bc3UnormBlock);
                        textureLibrary.Add(matInfo.MaskTexture, maskTexture);
                    }
                    material.SetTexture(maskProp, maskTexture);
                }
                else
                {
                    material.SetTexture(maskProp, EngineTextures.Black);
                }

                material.SetVector4(texColour, matInfo.DiffuseColour);
                material.SetFloat(texTiling, 1);

                material.SetFloat(exposureProp, 2.0f);
                material.SetFloat(gammaProp, 1.0f);


                for (int j = 0; j < matInfo.appliesTo.Count; j++, k++)
                {
                    var meshIndex = matInfo.appliesTo[j];
                    var entity = entityManager.CreateEntity();
                    children.Values[k] = entity;
                    entityManager.AddComponent(entity, parent);

                    AddRenderMeshComponents(entity, material, 0, sponza[meshIndex], entityManager);
                    entityManager.AddComponent(entity, new MaterialProviderComponent() { Value = materialProvider.Hash, LayerFlags = material.Pipeline.Transparent ? RenderLayer.Default | RenderLayer.Transparent : RenderLayer.Default });
                }
                litVariant++;
            }
            entityManager.AddComponent(commonParent, new Rotation() { Value = TransformExtensions.EulerUnity(00, 90, 0) });
            entityManager.AddComponent(commonParent, new Scale() { Value = Vector3.One });
            entityManager.AddComponent(commonParent, children);

           DeferredRenderer.SetExposure(2.0f);
           DeferredRenderer.SetGamma(1.0f);
        }

        public static void AddRenderMeshComponents(Entity entity, Material mat, int entityVariant, DirectSubMesh mesh, EntityManager entityManager, RenderLayer layerFlags = RenderLayer.Default)
        {
            entityManager.AddComponent<Translation>(entity);
            entityManager.AddComponent(entity, new RenderMesh()
            {
                Mesh = mesh.GetSubMeshIndex(),
                Material = new()
                {
                    Transparent = mat.Pipeline.Transparent,
                    Hash = mat.Pipeline.Hash,
                    Variant = (int)mat.VariantIndex,
                    Entity = entityVariant
                },
                LayerFlags = mat.Pipeline.Transparent ?  layerFlags | RenderLayer.Transparent : layerFlags
            });

            entityManager.AddComponent(entity, mesh.GetSubMeshIndex());
        }
    }
}
