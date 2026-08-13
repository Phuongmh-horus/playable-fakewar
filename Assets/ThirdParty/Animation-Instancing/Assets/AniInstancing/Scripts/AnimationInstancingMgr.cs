/*
THIS FILE IS PART OF Animation Instancing PROJECT
AnimationInstancing.cs - The core part of the Animation Instancing library

©2017 Jin Xiaoyu. All Rights Reserved.
*/

using UnityEngine;
using UnityEngine.Rendering;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace AnimationInstancing
{
    [AddComponentMenu("AnimationInstancingMgr")]
    public class AnimationInstancingMgr : Singleton<AnimationInstancingMgr>
    {
        private struct VertexCacheKey : IEquatable<VertexCacheKey>
        {
            private readonly int prototypeId;
            private readonly int meshId;
            private readonly int rendererPathHash;
            private readonly int attachmentHash;

            public VertexCacheKey(int prototypeId, int meshId, int rendererPathHash, int attachmentHash)
            {
                this.prototypeId = prototypeId;
                this.meshId = meshId;
                this.rendererPathHash = rendererPathHash;
                this.attachmentHash = attachmentHash;
            }

            public bool Equals(VertexCacheKey other)
            {
                return prototypeId == other.prototypeId &&
                    meshId == other.meshId &&
                    rendererPathHash == other.rendererPathHash &&
                    attachmentHash == other.attachmentHash;
            }

            public override bool Equals(object obj)
            {
                if (!(obj is VertexCacheKey)) return false;
                return Equals((VertexCacheKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = prototypeId;
                    hash = hash * 397 ^ meshId;
                    hash = hash * 397 ^ rendererPathHash;
                    return hash * 397 ^ attachmentHash;
                }
            }
        }

        internal struct MaterialKey : IEquatable<MaterialKey>
        {
            private readonly Material[] materials;

            public MaterialKey(Material[] materials)
            {
                this.materials = materials;
            }

            public bool Equals(MaterialKey other)
            {
                if (ReferenceEquals(materials, other.materials)) return true;
                if (materials == null || other.materials == null || materials.Length != other.materials.Length) return false;

                for (int index = 0; index < materials.Length; index++)
                {
                    if (materials[index] != other.materials[index]) return false;
                }

                return true;
            }

            public override bool Equals(object obj)
            {
                if (!(obj is MaterialKey)) return false;
                return Equals((MaterialKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = materials != null ? materials.Length : 0;
                    if (materials == null) return hash;

                    for (int index = 0; index < materials.Length; index++)
                    {
                        hash = hash * 397 ^ (materials[index] != null ? materials[index].GetInstanceID() : 0);
                    }

                    return hash;
                }
            }
        }

        // array[index base on texture][package index][instance index]
        public class InstanceData
        {
            public List<Matrix4x4[]>[] worldMatrix;
            public List<float[]>[] frameIndex;
            public List<float[]>[] preFrameIndex;
            public List<float[]>[] transitionProgress;
            public List<float[]>[] grayscale;
        }

        public class InstancingPackage
        {
            public Material[] material;
            public int animationTextureIndex = 0;
            public int subMeshCount = 1;
            public int instancingCount;
            public int size;
            public MaterialPropertyBlock propertyBlock;
        }
        public class MaterialBlock
        {
            public InstanceData instanceData;
            public int[] runtimePackageIndex;
            // array[index base on texture][package index]
            public List<InstancingPackage>[] packageList;
        }

        public class VertexCache
        {
            public int nameCode;
            public Mesh mesh = null;
            internal Dictionary<MaterialKey, MaterialBlock> instanceBlockList;
            public Vector4[] weight;
            public Vector4[] boneIndex;
            public Material[] materials = null;
            public Matrix4x4[] bindPose;
            public Transform[] bonePose;
            public int boneTextureIndex = -1;

            // these are temporary, should be moved to InstancingPackage
            public ShadowCastingMode shadowcastingMode;
            public bool receiveShadow;
            public int layer;
        }

        public class AnimationTexture
        {
            public string name { get; set; }
            public Texture2D[] boneTexture { get; set; }
            public int blockWidth { get; set; }
            public int blockHeight { get; set; }
        }

        // all object used animation instancing
        List<AnimationInstancing> aniInstancingList;
        // to calculate lod level
        private Transform cameraTransform; 
        private Dictionary<VertexCacheKey, VertexCache> vertexCachePool;
        private Dictionary<VertexCacheKey, InstanceData> instanceDataPool;
        const int InstancingSizePerPackage = 200;
        int instancingPackageSize = InstancingSizePerPackage;
        public int InstancingPackageSize
        {
            get { return instancingPackageSize; }
            set { instancingPackageSize = value; }
        }
        private List<AnimationTexture> animationTextureList = new List<AnimationTexture>();

        [SerializeField]
        private bool useInstancing = true;
        public bool UseInstancing
        {
            get { return useInstancing; }
            set { useInstancing = value; }
        }

        BoundingSphere[] boundingSphere;
        int usedBoundingSphereCount = 0;
        CullingGroup cullingGroup;

        public static AnimationInstancingMgr GetInstance()
        {
            return Instance;
        }

        private void OnEnable()
        {
            boundingSphere = new BoundingSphere[5000];
            InitializeCullingGroup();
            cameraTransform = Camera.main.transform;
            aniInstancingList = new List<AnimationInstancing>(1000);
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES2)
            {
                instancingPackageSize = 1;
                UseInstancing = false;
            }

			vertexCachePool = new Dictionary<VertexCacheKey, VertexCache>();
			instanceDataPool = new Dictionary<VertexCacheKey, InstanceData>();
        }

        private void Start()
        {
            
        }

        private void InitializeCullingGroup()
        {
            cullingGroup = new CullingGroup();
            cullingGroup.targetCamera = Camera.main;
            cullingGroup.onStateChanged = CullingStateChanged;
            cullingGroup.SetBoundingSpheres(boundingSphere);
            usedBoundingSphereCount = 0;
            cullingGroup.SetBoundingSphereCount(usedBoundingSphereCount);
        }

        void Update()
        {
            ApplyBoneMatrix();
            Render();
        }

        private void Render()
        {
            foreach (var obj in vertexCachePool)
            {
                VertexCache vertexCache = obj.Value;
                foreach (var block in vertexCache.instanceBlockList)
                {
                    List<InstancingPackage>[] packageList = block.Value.packageList;
                    for (int k = 0; k != packageList.Length; ++k)
                    {
                        for (int i = 0; i != packageList[k].Count; ++i)
                        {
                            InstancingPackage package = packageList[k][i];
                            if (package.instancingCount == 0)
                                continue;
                            InstanceData data = block.Value.instanceData;
                            if (useInstancing)
                            {
#if UNITY_EDITOR
                                PreparePackageMaterial(package, vertexCache, k);
#endif
                                package.propertyBlock.SetFloatArray("frameIndex", data.frameIndex[k][i]);
                                package.propertyBlock.SetFloatArray("preFrameIndex", data.preFrameIndex[k][i]);
                                package.propertyBlock.SetFloatArray("transitionProgress", data.transitionProgress[k][i]);
                                package.propertyBlock.SetFloatArray("_Grayscale", data.grayscale[k][i]);
                            }

                            for (int j = 0; j != package.subMeshCount; ++j)
                            {
                                if (useInstancing)
                                {
                                    Graphics.DrawMeshInstanced(vertexCache.mesh,
                                        j,
                                        package.material[j],
                                        data.worldMatrix[k][i],
                                        package.instancingCount,
                                        package.propertyBlock,
                                        vertexCache.shadowcastingMode,
                                        vertexCache.receiveShadow,
                                        vertexCache.layer);
                                }
                                else
                                {
                                    package.material[j].SetFloat("frameIndex", data.frameIndex[k][i][0]);
                                    package.material[j].SetFloat("preFrameIndex", data.preFrameIndex[k][i][0]);
                                    package.material[j].SetFloat("transitionProgress", data.transitionProgress[k][i][0]);
                                    package.material[j].SetFloat("_Grayscale", data.grayscale[k][i][0]);
                                    Graphics.DrawMesh(vertexCache.mesh,
                                        data.worldMatrix[k][i][0],
                                        package.material[j],
                                        0,
                                        null,
                                        j);
                                }
                            }
                            package.instancingCount = 0;
                        }
                        block.Value.runtimePackageIndex[k] = 0;
                    }
                }

//                 if (obj.Value.instancingData == null)
//                     continue;
//                 vertexCache.bufInstance.SetData(obj.Value.instancingData);
// 
//                 for (int i = 0; i != vertexCache.subMeshCount; ++i)
//                 {
//                     Material material = vertexCache.instanceMaterial[i];
//                     material.SetBuffer("buf_InstanceMatrices", vertexCache.bufInstance);
//                     vertexCache.args[i][1] = (uint)vertexCache.currentInstancingIndex;
//                     vertexCache.bufArgs[i].SetData(vertexCache.args[i]);
// 
//                     Graphics.DrawMeshInstancedIndirect(vertexCache.mesh,
//                                     i,
//                                     vertexCache.instanceMaterial[i],
//                                     new Bounds(Vector3.zero, new Vector3(10000.0f, 10000.0f, 10000.0f)),
//                                     vertexCache.bufArgs[i]);
//                 }
//                 vertexCache.currentInstancingIndex = 0;
            }
        }

        public void Clear()
        {
            aniInstancingList.Clear();
            cullingGroup.Dispose();
            vertexCachePool.Clear();
            instanceDataPool.Clear();
            InitializeCullingGroup();
        }

        public GameObject CreateInstance(GameObject prefab)
        {
            Debug.Assert(prefab != null);
            GameObject obj = Instantiate(prefab, Vector3.zero, Quaternion.identity);
            AnimationInstancing script = obj.GetComponent<AnimationInstancing>();
            AnimationInstancing prototypeScript = prefab.GetComponent<AnimationInstancing>();
            script.prototype = prototypeScript.prototype;
            return obj;
        }

        public void AddInstance(GameObject obj)
        {
            AnimationInstancing script = obj.GetComponent<AnimationInstancing>();
            Debug.Assert(script != null);
            if (script == null)
            {
                Debug.LogError("The prefab you created doesn't attach the script 'AnimationInstancing'.");
                Destroy(obj);
                return;
            }

            if (aniInstancingList.Count >= boundingSphere.Length)
            {
                Debug.LogError("Animation Instancing reached the culling capacity of " + boundingSphere.Length + ".", script);
                script.enabled = false;
                return;
            }

            try
            {
                bool success = script.InitializeAnimation();
                if (success)
                {
                    aniInstancingList.Add(script);
                    boundingSphere[usedBoundingSphereCount] = script.boundingSpere;
                    usedBoundingSphereCount++;
                    cullingGroup.SetBoundingSphereCount(usedBoundingSphereCount);
                    script.visible = cullingGroup.IsVisible(usedBoundingSphereCount - 1);
                }
                else
                {
                    script.enabled = false;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e, script);
                Debug.Log("Initialize animation failed. Please check out the backed animation infos and regenerate it.");
                script.enabled = false;
            }
        }

        public void RemoveInstance(AnimationInstancing instance)
        {
            Debug.Assert(aniInstancingList != null);
            if (aniInstancingList == null || cullingGroup == null) return;

            int index = aniInstancingList.IndexOf(instance);
            if (index < 0 || index >= usedBoundingSphereCount) return;

            int lastIndex = aniInstancingList.Count - 1;
            if (index != lastIndex) aniInstancingList[index] = aniInstancingList[lastIndex];
            aniInstancingList.RemoveAt(lastIndex);
            cullingGroup.EraseSwapBack(index);
            CullingGroup.EraseSwapBack(index, boundingSphere, ref usedBoundingSphereCount);
        }

        void OnDisable()
        {
            ReleaseBuffer();
            cullingGroup.Dispose();
            cullingGroup = null;
        }

#if !UNITY_ANDROID && !UNITY_IPHONE
        private void OnApplicationFocus(bool focus)
        {
            if (focus)
            {
                RefreshMaterial();
            }
        }
#endif

        void RefreshMaterial()
        {
            if (vertexCachePool == null)
                return;

            foreach (var obj in vertexCachePool)
            {
                VertexCache cache = obj.Value;
                foreach (var block in cache.instanceBlockList)
                {
                    for (int j = 0; j != block.Value.packageList.Length; ++j)
                    {
                        for (int k = 0; k != block.Value.packageList[j].Count; ++k)
                        {
                            InstancingPackage package = block.Value.packageList[j][k];
                            PreparePackageMaterial(package, cache, j);
                        }
                    }
                }
                
            }
        }
       
        void ApplyBoneMatrix()
        {
            Vector3 cameraPosition = cameraTransform.position;
            for (int i = 0; i != aniInstancingList.Count; ++i)
            {
                AnimationInstancing instance = aniInstancingList[i];
                AnimationInstancing transformSource = instance.parentInstance != null ? instance.parentInstance : instance;
                if (!instance.IsPlaying())
                    continue;
                if (instance.aniIndex < 0 && instance.parentInstance == null)
                    continue;

                if (instance.applyRootMotion)
                    ApplyRootMotion(instance);

                instance.UpdateAnimation();
                instance.boundingSpere.position = transformSource.worldTransform.position;
                boundingSphere[i] = instance.boundingSpere;

                if (!instance.visible)
                    continue;
                if (instance.parentInstance != null)
                    instance.lodLevel = Mathf.Min(instance.parentInstance.lodLevel, instance.lodInfo.Length - 1);
                else
                    instance.UpdateLod(cameraPosition);

                AnimationInstancing.LodInfo lod = instance.lodInfo[instance.lodLevel];
                int aniTextureIndex = -1;
                if (instance.parentInstance != null)
                    aniTextureIndex = instance.parentInstance.aniTextureIndex;
                else
                    aniTextureIndex = instance.aniTextureIndex;

                for (int j = 0; j != lod.vertexCacheList.Length; ++j)
                {
                    VertexCache cache = lod.vertexCacheList[j];
                    MaterialBlock block = lod.materialBlockList[j];
                    Debug.Assert(block != null);
                    int packageIndex = block.runtimePackageIndex[aniTextureIndex];
                    Debug.Assert(packageIndex < block.packageList[aniTextureIndex].Count);
                    InstancingPackage package = block.packageList[aniTextureIndex][packageIndex];
                    if (package.instancingCount + 1 > instancingPackageSize)
                    {
                        ++block.runtimePackageIndex[aniTextureIndex];
                        packageIndex = block.runtimePackageIndex[aniTextureIndex];
                        if (packageIndex >= block.packageList[aniTextureIndex].Count)
                        {
                            InstancingPackage newPackage = CreatePackage(block.instanceData,
                                cache.mesh,
                                cache.materials,
                                aniTextureIndex);
                            block.packageList[aniTextureIndex].Add(newPackage);
                            PreparePackageMaterial(newPackage, cache, aniTextureIndex);
                            newPackage.instancingCount = 1;
                        }
                        block.packageList[aniTextureIndex][packageIndex].instancingCount = 1;
                    }
                    else
                        ++package.instancingCount;

                    {
                        VertexCache vertexCache = cache;
                        InstanceData data = block.instanceData;
                        int index = block.runtimePackageIndex[aniTextureIndex];
                        InstancingPackage pkg = block.packageList[aniTextureIndex][index];
                        int count = pkg.instancingCount - 1;
                        if (count >= 0)
                        {
                            Matrix4x4 worldMat = transformSource.worldTransform.localToWorldMatrix;
                            Matrix4x4[] arrayMat = data.worldMatrix[aniTextureIndex][index];
                            arrayMat[count].m00 = worldMat.m00;
                            arrayMat[count].m01 = worldMat.m01;
                            arrayMat[count].m02 = worldMat.m02;
                            arrayMat[count].m03 = worldMat.m03;
                            arrayMat[count].m10 = worldMat.m10;
                            arrayMat[count].m11 = worldMat.m11;
                            arrayMat[count].m12 = worldMat.m12;
                            arrayMat[count].m13 = worldMat.m13;
                            arrayMat[count].m20 = worldMat.m20;
                            arrayMat[count].m21 = worldMat.m21;
                            arrayMat[count].m22 = worldMat.m22;
                            arrayMat[count].m23 = worldMat.m23;
                            arrayMat[count].m30 = worldMat.m30;
                            arrayMat[count].m31 = worldMat.m31;
                            arrayMat[count].m32 = worldMat.m32;
                            arrayMat[count].m33 = worldMat.m33;
                            float frameIndex = 0, preFrameIndex = -1, transition = 0f;
                            if (instance.parentInstance != null)
                            {
                                frameIndex = instance.parentInstance.aniInfo[instance.parentInstance.aniIndex].animationIndex + instance.parentInstance.curFrame;
                                if (instance.parentInstance.preAniIndex >= 0)
                                    preFrameIndex = instance.parentInstance.aniInfo[instance.parentInstance.preAniIndex].animationIndex + instance.parentInstance.preAniFrame;
                                transition = instance.parentInstance.transitionProgress;
                            }
                            else
                            {
                                frameIndex = instance.aniInfo[instance.aniIndex].animationIndex + instance.curFrame;
                                if (instance.preAniIndex >= 0)
                                    preFrameIndex = instance.aniInfo[instance.preAniIndex].animationIndex + instance.preAniFrame;
                                transition = instance.transitionProgress;
                            }
                            data.frameIndex[aniTextureIndex][index][count] = frameIndex;
                            data.preFrameIndex[aniTextureIndex][index][count] = preFrameIndex;
                            data.transitionProgress[aniTextureIndex][index][count] = transition;
                            AnimationInstancing appearanceSource = instance.parentInstance != null
                                ? instance.parentInstance
                                : instance;
                            data.grayscale[aniTextureIndex][index][count] = appearanceSource.grayscale;
                        }
                    }
                }
            }
        }


        private void ApplyRootMotion(AnimationInstancing instance)
        {
            AnimationInfo info = instance.GetCurrentAnimationInfo();
            if (info == null || !info.rootMotion)
                return;

            int preSampleFrame = (int)instance.curFrame;
            int nextSampleFrame = (int)(instance.curFrame + 1.0f);
            if (nextSampleFrame >= info.totalFrame)
                return;

            Vector3 preVelocity = info.velocity[preSampleFrame];
            Vector3 nextVelocity = info.velocity[nextSampleFrame];
            Vector3 velocity = Vector3.Lerp(preVelocity, nextVelocity, instance.curFrame - preSampleFrame);
            Vector3 angularVelocity = Vector3.Lerp(info.angularVelocity[preSampleFrame], info.angularVelocity[nextSampleFrame], instance.curFrame - preSampleFrame);

            {
                Quaternion localQuaternion = instance.worldTransform.localRotation;
                Quaternion delta = Quaternion.Euler(angularVelocity * Time.deltaTime);
                localQuaternion = localQuaternion * delta;

                Vector3 offset = velocity * Time.deltaTime;
                offset = localQuaternion * offset;
                //offset.y = 0.0f;
                Vector3 localPosition = instance.worldTransform.localPosition;
                localPosition += offset;
#if UNITY_5_6_OR_NEWER
                instance.worldTransform.SetPositionAndRotation(localPosition, localQuaternion);
#else
                instance.worldTransform.localPosition = localPosition;
                instance.worldTransform.localRotation = localQuaternion;
#endif
            }
        }

        private int FindTexture_internal(string name)
        {
            for (int i = 0; i != animationTextureList.Count; ++i)
            {
                AnimationTexture texture = animationTextureList[i] as AnimationTexture;
                if (texture.name == name)
                {
                    return i;
                }
            }
            return -1;
        }

        public AnimationTexture FindTexture(string name)
        {
            int index = FindTexture_internal(name);
            if (index >= 0)
                return animationTextureList[index];
            return null;
        }


        public AnimationTexture FindTexture(int index)
        {
            if (0 <= index && index < animationTextureList.Count)
            {
                return animationTextureList[index];
            }
            return null;
        }


        public VertexCache FindVertexCache(int renderName)
        {
            foreach (VertexCache cache in vertexCachePool.Values)
            {
                if (cache.nameCode == renderName)
                    return cache;
            }

            return null;
        }

        private void ReadTexture(BinaryReader reader, string prefabName)
        {
            TextureFormat format = TextureFormat.RGBAHalf;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES2)
            {
                //todo
                format = TextureFormat.RGBA32;
            }
            int count = reader.ReadInt32();
            int blockWidth = reader.ReadInt32();
            int blockHeight = reader.ReadInt32();

            AnimationTexture aniTexture = new AnimationTexture();
            aniTexture.boneTexture = new Texture2D[count];
            aniTexture.name = prefabName;
            aniTexture.blockWidth = blockWidth;
            aniTexture.blockHeight = blockHeight;
            animationTextureList.Add(aniTexture);

            for (int i = 0; i != count; ++i)
            {
                int textureWidth = reader.ReadInt32();
                int textureHeight = reader.ReadInt32();
                int byteLength = reader.ReadInt32();
                byte[] b = new byte[byteLength];
                b = reader.ReadBytes(byteLength);
                Texture2D texture = new Texture2D(textureWidth, textureHeight, format, false);
                texture.LoadRawTextureData(b);
                texture.filterMode = FilterMode.Point;
                texture.Apply();
                aniTexture.boneTexture[i] = texture;
            }
        }

        public bool ImportAnimationTexture(string prefabName, BinaryReader reader)
        {
            if (FindTexture_internal(prefabName) >= 0)
            {
                return true;
            }

            ReadTexture(reader, prefabName);
            return true;
        }

        private void ReleaseBuffer()
        {
            if (vertexCachePool != null)
                vertexCachePool.Clear();
        }


        public InstancingPackage CreatePackage(InstanceData data, Mesh mesh, Material[] originalMaterial, int animationIndex)
        {
            InstancingPackage package = new InstancingPackage();
            package.material = new Material[mesh.subMeshCount];
            package.subMeshCount = mesh.subMeshCount;
            package.size = 1;
            for (int i = 0; i != mesh.subMeshCount; ++i)
            {
                package.material[i] = new Material(originalMaterial[i]);
#if UNITY_5_6_OR_NEWER
                package.material[i].enableInstancing = UseInstancing;
#else
                if (UseInstancing)
                    package.material[i].EnableKeyword("INSTANCING_ON");
                else
                    package.material[i].DisableKeyword("INSTANCING_ON");
#endif

                package.propertyBlock = new MaterialPropertyBlock();
                package.material[i].EnableKeyword("USE_CONSTANT_BUFFER");
                package.material[i].DisableKeyword("USE_COMPUTE_BUFFER");
            }

            Matrix4x4[] mat = new Matrix4x4[instancingPackageSize];
            float[] frameIndex = new float[instancingPackageSize];
            float[] preFrameIndex = new float[instancingPackageSize];
            float[] transitionProgress = new float[instancingPackageSize];
            float[] grayscale = new float[instancingPackageSize];
            data.worldMatrix[animationIndex].Add(mat);
            data.frameIndex[animationIndex].Add(frameIndex);
            data.preFrameIndex[animationIndex].Add(preFrameIndex);
            data.transitionProgress[animationIndex].Add(transitionProgress);
            data.grayscale[animationIndex].Add(grayscale);
            return package;
        }

        InstanceData CreateInstanceData(int packageCount)
        {
            InstanceData data = new InstanceData();
            data.worldMatrix = new List<Matrix4x4[]>[packageCount];
            data.frameIndex = new List<float[]>[packageCount];
            data.preFrameIndex = new List<float[]>[packageCount];
            data.transitionProgress = new List<float[]>[packageCount];
            data.grayscale = new List<float[]>[packageCount];
            for (int i = 0; i != packageCount; ++i)
            {
                data.worldMatrix[i] = new List<Matrix4x4[]>();
                data.frameIndex[i] = new List<float[]>();
                data.preFrameIndex[i] = new List<float[]>();
                data.transitionProgress[i] = new List<float[]>();
                data.grayscale[i] = new List<float[]>();
            }   
            return data;    
        }


        // alias is to use for attachment, it should be a bone name
        public void AddMeshVertex(string prefabName,
            int prototypeId,
            AnimationInstancing.LodInfo[] lodInfo,
            Transform[] bones,
            List<Matrix4x4> bindPose,
            int bonePerVertex,
            string alias = null,
            Transform instanceRoot = null)
        {
            UnityEngine.Profiling.Profiler.BeginSample("AddMeshVertex()");
            for (int x = 0; x != lodInfo.Length; ++x)
            {
                AnimationInstancing.LodInfo lod = lodInfo[x];
                for (int i = 0; i != lod.skinnedMeshRenderer.Length; ++i)
                {
                    Mesh m = lod.skinnedMeshRenderer[i].sharedMesh;
                    if (m == null)
                        continue;

                    SkinnedMeshRenderer renderer = lod.skinnedMeshRenderer[i];
                    int nameCode = renderer.name.GetHashCode();
                    VertexCacheKey cacheKey = new VertexCacheKey(prototypeId,
                        m.GetInstanceID(),
                        GetRendererPathHash(renderer, instanceRoot),
                        0);
                    MaterialKey materialKey = new MaterialKey(renderer.sharedMaterials);
                    VertexCache cache = null;
                    if (vertexCachePool.TryGetValue(cacheKey, out cache))
                    {
                        MaterialBlock block = null;
                        if (!cache.instanceBlockList.TryGetValue(materialKey, out block))
                        {
                            block = CreateBlock(cache, renderer.sharedMaterials);
                            cache.instanceBlockList.Add(materialKey, block);
                        }
                        lod.vertexCacheList[i] = cache;
                        lod.materialBlockList[i] = block;
                        continue;
                    }

                    Mesh instancedMesh = Instantiate(m); // fix xung đột khi khởi tạo nhiều prefab có dùng chung shared mesh
                    VertexCache vertexCache = CreateVertexCache(prefabName, cacheKey, nameCode, instancedMesh);
                    vertexCache.bindPose = bindPose.ToArray();
                    MaterialBlock matBlock = CreateBlock(vertexCache, renderer.sharedMaterials);
                    vertexCache.instanceBlockList.Add(materialKey, matBlock);
                    SetupVertexCache(vertexCache, matBlock, renderer, bones, bonePerVertex);
                    lod.vertexCacheList[i] = vertexCache;
                    lod.materialBlockList[i] = matBlock;
                }

                for (int i = 0, j = lod.skinnedMeshRenderer.Length; i != lod.meshRenderer.Length; ++i, ++j)
                {
                    Mesh m = lod.meshFilter[i].sharedMesh;
                    if (m == null)
                        continue;

                    MeshRenderer renderer = lod.meshRenderer[i];
                    int renderName = renderer.name.GetHashCode();
                    VertexCacheKey cacheKey = new VertexCacheKey(prototypeId,
                        m.GetInstanceID(),
                        GetRendererPathHash(renderer, instanceRoot),
                        alias != null ? alias.GetHashCode() : 0);
                    MaterialKey materialKey = new MaterialKey(renderer.sharedMaterials);
                    VertexCache cache = null;
                    if (vertexCachePool.TryGetValue(cacheKey, out cache))
                    { 
                        MaterialBlock block = null;
                        if (!cache.instanceBlockList.TryGetValue(materialKey, out block))
                        {
                            block = CreateBlock(cache, renderer.sharedMaterials);
                            cache.instanceBlockList.Add(materialKey, block);
                        }
                        lod.vertexCacheList[j] = cache;
                        lod.materialBlockList[j] = block;
                        continue;
                    }

                    VertexCache vertexCache = CreateVertexCache(prefabName, cacheKey, renderName, m);
                    if (bindPose != null)
                        vertexCache.bindPose = bindPose.ToArray();
                    MaterialBlock matBlock = CreateBlock(vertexCache, renderer.sharedMaterials);
                    vertexCache.instanceBlockList.Add(materialKey, matBlock);
                    SetupVertexCache(vertexCache, matBlock, renderer, m, bones, bonePerVertex);
                    lod.vertexCacheList[lod.skinnedMeshRenderer.Length + i] = vertexCache;
                    lod.materialBlockList[lod.skinnedMeshRenderer.Length + i] = matBlock;
                }
            }

            UnityEngine.Profiling.Profiler.EndSample();
        }

        // Builds or reuses static attachment meshes in the target bone's bind space.
        // Each renderer keeps its authored hierarchy transform, while the cache key separates
        // different parent skeletons, bones, source meshes and attachment poses.
        public bool AddAttachmentMeshVertex(string prefabName,
            int attachmentIdentity,
            AnimationInstancing.LodInfo[] lodInfo,
            VertexCache parentCache,
            Transform targetBone,
            int boneIndex,
            int parentIdentity)
        {
            UnityEngine.Profiling.Profiler.BeginSample("AddAttachmentMeshVertex()");
            if (parentCache == null || parentCache.bindPose == null || boneIndex < 0 || boneIndex >= parentCache.bindPose.Length)
            {
                Debug.LogError("The attachment bone doesn't have a valid parent bind pose.");
                UnityEngine.Profiling.Profiler.EndSample();
                return false;
            }
            if (targetBone == null)
            {
                Debug.LogError("The attachment target bone is null.");
                UnityEngine.Profiling.Profiler.EndSample();
                return false;
            }

            bool hasMesh = false;
            for (int x = 0; x != lodInfo.Length; ++x)
            {
                AnimationInstancing.LodInfo lod = lodInfo[x];
                if (lod.meshFilter == null || lod.meshFilter.Length != lod.meshRenderer.Length)
                {
                    Debug.LogError("The attachment MeshRenderer and MeshFilter data don't match.");
                    UnityEngine.Profiling.Profiler.EndSample();
                    return false;
                }

                for (int i = 0; i != lod.meshRenderer.Length; ++i)
                {
                    MeshRenderer render = lod.meshRenderer[i];
                    MeshFilter meshFilter = lod.meshFilter[i];
                    Mesh sharedMesh = meshFilter.sharedMesh;
                    if (sharedMesh == null)
                        continue;

                    hasMesh = true;
                    Matrix4x4 rendererToBone = targetBone.worldToLocalMatrix * meshFilter.transform.localToWorldMatrix;
                    int attachmentHash;
                    unchecked
                    {
                        attachmentHash = parentIdentity;
                        attachmentHash = attachmentHash * 397 ^ parentCache.boneTextureIndex;
                        attachmentHash = attachmentHash * 397 ^ boneIndex;
                        attachmentHash = attachmentHash * 397 ^ rendererToBone.GetHashCode();
                    }

                    VertexCacheKey cacheKey = new VertexCacheKey(attachmentIdentity,
                        sharedMesh.GetInstanceID(),
                        GetRendererPathHash(render),
                        attachmentHash);
                    MaterialKey materialKey = new MaterialKey(render.sharedMaterials);
                    VertexCache cache = null;
                    MaterialBlock block = null;
                    if (vertexCachePool.TryGetValue(cacheKey, out cache))
                    {
                        if (!cache.instanceBlockList.TryGetValue(materialKey, out block))
                        {
                            block = CreateBlock(cache, render.sharedMaterials);
                            cache.instanceBlockList.Add(materialKey, block);
                        }
                    }
                    else
                    {
                        cache = CreateVertexCache(prefabName, cacheKey, render.name.GetHashCode(), sharedMesh);
                        cache.boneTextureIndex = parentCache.boneTextureIndex;
                        BindAttachment(parentCache, cache, sharedMesh, boneIndex, rendererToBone);
                        cache.materials = render.sharedMaterials;
                        cache.shadowcastingMode = render.shadowCastingMode;
                        cache.receiveShadow = render.receiveShadows;
                        cache.layer = render.gameObject.layer;
                        SetupAdditionalData(cache);

                        block = CreateBlock(cache, render.sharedMaterials);
                        cache.instanceBlockList.Add(materialKey, block);
                    }

                    int cacheIndex = lod.skinnedMeshRenderer.Length + i;
                    lod.vertexCacheList[cacheIndex] = cache;
                    lod.materialBlockList[cacheIndex] = block;
                }
            }

            UnityEngine.Profiling.Profiler.EndSample();
            return hasMesh;
        }

        int GetPackageCount(VertexCache vertexCache)
        {
            int packageCount = 1;
            if (vertexCache.boneTextureIndex >= 0)
            {
                AnimationTexture texture = animationTextureList[vertexCache.boneTextureIndex];
                packageCount = texture.boneTexture.Length;
            }
            return packageCount;
        }

        MaterialBlock CreateBlock(VertexCache cache, Material[] materials)
        {
            MaterialBlock block = new MaterialBlock();
            int packageCount = GetPackageCount(cache);
            block.instanceData = CreateInstanceData(packageCount);                             
            block.packageList = new List<InstancingPackage>[packageCount];
            for (int i = 0; i != block.packageList.Length; ++i)
            {
               block.packageList[i] = new List<InstancingPackage>();

               InstancingPackage package = CreatePackage(block.instanceData, 
                    cache.mesh,
                    materials, 
                    i);
                block.packageList[i].Add(package);
                PreparePackageMaterial(package, cache, i);
                package.instancingCount = 1;
            }
            block.runtimePackageIndex = new int[packageCount];
            return block;
        }

        private VertexCache CreateVertexCache(string prefabName, VertexCacheKey cacheKey, int renderName, Mesh mesh)
        {
            VertexCache vertexCache = new VertexCache();
            vertexCachePool[cacheKey] = vertexCache;
            vertexCache.nameCode = renderName;
            vertexCache.mesh = mesh;
            vertexCache.boneTextureIndex = FindTexture_internal(prefabName);
            vertexCache.weight = new Vector4[mesh.vertexCount];
            vertexCache.boneIndex = new Vector4[mesh.vertexCount];
            int packageCount = GetPackageCount(vertexCache);
            InstanceData data = null;
            if (!instanceDataPool.TryGetValue(cacheKey, out data))
            {
                data = CreateInstanceData(packageCount);
                instanceDataPool.Add(cacheKey, data);
            }
            vertexCache.instanceBlockList = new Dictionary<MaterialKey, MaterialBlock>();
            return vertexCache;
        }

        private static int GetRendererPathHash(Renderer renderer, Transform instanceRoot = null)
        {
            unchecked
            {
                int hash = 17;
                Transform current = renderer.transform;
                while (current != null && current != instanceRoot && current.parent != null)
                {
                    hash = hash * 397 ^ current.GetSiblingIndex();
                    hash = hash * 397 ^ current.name.GetHashCode();
                    current = current.parent;
                }

                return hash;
            }
        }
        private void SetupVertexCache(VertexCache vertexCache,
            MaterialBlock block,
            SkinnedMeshRenderer render,
            Transform[] boneTransform,
            int bonePerVertex)
        {
            int[] boneIndex = null;
            if (render.bones.Length != boneTransform.Length)
            {
                if (render.bones.Length == 0)
                {
                    boneIndex = new int[1];
                    int hashRenderParentName = render.transform.parent.name.GetHashCode();
                    for (int k = 0; k != boneTransform.Length; ++k)
                    {
                        if (hashRenderParentName == boneTransform[k].name.GetHashCode())
                        {
                            boneIndex[0] = k;
                            break;
                        }
                    }
                }
                else
                {
                    boneIndex = new int[render.bones.Length];
                    for (int j = 0; j != render.bones.Length; ++j)
                    {
                        boneIndex[j] = -1;
                        Transform trans = render.bones[j];
                        int hashTransformName = trans.name.GetHashCode();
                        for (int k = 0; k != boneTransform.Length; ++k)
                        {
                            if (hashTransformName == boneTransform[k].name.GetHashCode())
                            {
                                boneIndex[j] = k;
                                break;
                            }
                        }
                    }

                    if (boneIndex.Length == 0)
                    {
                        boneIndex = null;
                    }
                }
            }

            UnityEngine.Profiling.Profiler.BeginSample("Copy the vertex data in SetupVertexCache()");
            Mesh m = render.sharedMesh;
            BoneWeight[] boneWeights = m.boneWeights;
            Debug.Assert(boneWeights.Length > 0);
            for (int j = 0; j != m.vertexCount; ++j)
            {
                vertexCache.weight[j].x = boneWeights[j].weight0;
                Debug.Assert(vertexCache.weight[j].x > 0.0f);
                vertexCache.weight[j].y = boneWeights[j].weight1;
                vertexCache.weight[j].z = boneWeights[j].weight2;
                vertexCache.weight[j].w = boneWeights[j].weight3;
                vertexCache.boneIndex[j].x
                    = boneIndex == null ? boneWeights[j].boneIndex0 : boneIndex[boneWeights[j].boneIndex0];
                vertexCache.boneIndex[j].y
                    = boneIndex == null ? boneWeights[j].boneIndex1 : boneIndex[boneWeights[j].boneIndex1];
                vertexCache.boneIndex[j].z
                    = boneIndex == null ? boneWeights[j].boneIndex2 : boneIndex[boneWeights[j].boneIndex2];
                vertexCache.boneIndex[j].w
                    = boneIndex == null ? boneWeights[j].boneIndex3 : boneIndex[boneWeights[j].boneIndex3];
                Debug.Assert(vertexCache.boneIndex[j].x >= 0);
                if (bonePerVertex == 3)
                {
                    float rate = 1.0f / (vertexCache.weight[j].x + vertexCache.weight[j].y + vertexCache.weight[j].z);
                    vertexCache.weight[j].x = vertexCache.weight[j].x * rate;
                    vertexCache.weight[j].y = vertexCache.weight[j].y * rate;
                    vertexCache.weight[j].z = vertexCache.weight[j].z * rate;
                    vertexCache.weight[j].w = -0.1f;
                }
                else if (bonePerVertex == 2)
                {
                    float rate = 1.0f / (vertexCache.weight[j].x + vertexCache.weight[j].y);
                    vertexCache.weight[j].x = vertexCache.weight[j].x * rate;
                    vertexCache.weight[j].y = vertexCache.weight[j].y * rate;
                    vertexCache.weight[j].z = -0.1f;
                    vertexCache.weight[j].w = -0.1f;
                }
                else if (bonePerVertex == 1)
                {
                    vertexCache.weight[j].x = 1.0f;
                    vertexCache.weight[j].y = -0.1f;
                    vertexCache.weight[j].z = -0.1f;
                    vertexCache.weight[j].w = -0.1f;
                }
            }
            UnityEngine.Profiling.Profiler.EndSample();

            if (vertexCache.materials == null)
                vertexCache.materials = render.sharedMaterials;
            SetupAdditionalData(vertexCache);
            for (int i = 0; i != block.packageList.Length; ++i)
            {
                InstancingPackage package = CreatePackage(block.instanceData, vertexCache.mesh, render.sharedMaterials, i);
                block.packageList[i].Add(package);
                //vertexCache.packageList[i].Add(package);
                PreparePackageMaterial(package, vertexCache, i);
            }
        }


        private void SetupVertexCache(VertexCache vertexCache,
            MaterialBlock block,
            MeshRenderer render,
            Mesh mesh,
            Transform[] boneTransform,
            int bonePerVertex)
        {
            int boneIndex = -1;
            if (boneTransform != null)
            {
                for (int k = 0; k != boneTransform.Length; ++k)
                {
                    if (render.transform.parent.name.GetHashCode() == boneTransform[k].name.GetHashCode())
                    {
                        boneIndex = k;
                        break;
                    }
                }
            }
            if (boneIndex >= 0)
            {
                //todo
                BindAttachment(vertexCache, vertexCache, vertexCache.mesh, boneIndex, Matrix4x4.identity);
            }
            if (vertexCache.materials == null)
                vertexCache.materials = render.sharedMaterials;
            SetupAdditionalData(vertexCache);
            for (int i = 0; i != block.packageList.Length; ++i)
            {
                InstancingPackage package = CreatePackage(block.instanceData, vertexCache.mesh, render.sharedMaterials, i);
                block.packageList[i].Add(package);
                PreparePackageMaterial(package, vertexCache, i);
            }
        }


        public void SetupAdditionalData(VertexCache vertexCache)
        {
            Color[] colors = new Color[vertexCache.weight.Length];            
            for (int i = 0; i != colors.Length; ++i)
            {
                colors[i].r = vertexCache.weight[i].x;
                colors[i].g = vertexCache.weight[i].y;
                colors[i].b = vertexCache.weight[i].z;
                colors[i].a = vertexCache.weight[i].w;
            }
            vertexCache.mesh.colors = colors;

            List<Vector4> uv2 = new List<Vector4>(vertexCache.boneIndex.Length);
            for (int i = 0; i != vertexCache.boneIndex.Length; ++i)
            {
                uv2.Add(vertexCache.boneIndex[i]);
            }
            vertexCache.mesh.SetUVs(2, uv2);
            vertexCache.mesh.UploadMeshData(false);
        }

        public void PreparePackageMaterial(InstancingPackage package, VertexCache vertexCache, int aniTextureIndex)
        {
            if (vertexCache.boneTextureIndex < 0)
                return;
                
            for (int i = 0; i != package.subMeshCount; ++i)
            {
                AnimationTexture texture = animationTextureList[vertexCache.boneTextureIndex];
                package.material[i].SetTexture("_boneTexture", texture.boneTexture[aniTextureIndex]);
                package.material[i].SetInt("_boneTextureWidth", texture.boneTexture[aniTextureIndex].width);
                package.material[i].SetInt("_boneTextureHeight", texture.boneTexture[aniTextureIndex].height);
                package.material[i].SetInt("_boneTextureBlockWidth", texture.blockWidth);
                package.material[i].SetInt("_boneTextureBlockHeight", texture.blockHeight);
            }
        }


        private void CullingStateChanged(CullingGroupEvent evt)
        {
            if (evt.index < 0 ||
                evt.index >= usedBoundingSphereCount ||
                evt.index >= aniInstancingList.Count)
            {
                return;
            }

            AnimationInstancing instance = aniInstancingList[evt.index];
            if (instance == null) return;

            if (evt.hasBecomeVisible)
            {
                if (instance.isActiveAndEnabled)
                {
                    instance.visible = true;
                }
            }
            if (evt.hasBecomeInvisible)
            {
                instance.visible = false;
            }
        }


        public void BindAttachment(VertexCache parentCache,
            VertexCache attachmentCache,
            Mesh sharedMesh,
            int boneIndex,
            Matrix4x4 rendererToBone)
        {
            Matrix4x4 bindMatrix = parentCache.bindPose[boneIndex].inverse * rendererToBone;
            attachmentCache.mesh = Instantiate(sharedMesh);
            Vector3[] vertices = attachmentCache.mesh.vertices;
            for (int k = 0; k != attachmentCache.mesh.vertexCount; ++k)
            {
                vertices[k] = bindMatrix.MultiplyPoint3x4(vertices[k]);
            }
            attachmentCache.mesh.vertices = vertices;
            attachmentCache.mesh.RecalculateBounds();

            for (int j = 0; j != attachmentCache.mesh.vertexCount; ++j)
            {
                attachmentCache.weight[j].x = 1.0f;
                attachmentCache.weight[j].y = -0.1f;
                attachmentCache.weight[j].z = -0.1f;
                attachmentCache.weight[j].w = -0.1f;
                attachmentCache.boneIndex[j].x = boneIndex;
            }
        }
    }
}
