using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace OptimizedFeature.Scripts.Editor
{
    public enum ShaderPatchMode
    {
        AutoPatchIfMissing,
        Ignore,
        AlwaysForcePatch
    }

    public enum VATBakeOutputMode
    {
        // Splits source renderer material slots only; it does not split VAT data.
        PerSkinnedMesh,
        Combined
    }

    /// <summary>
    /// Editor Tool Window implementing the VAT Bake process with automatic references discovery,
    /// selective animation clip baking and Material/Shader validation displays.
    /// </summary>
    public class VATBakeToolWindow : EditorWindow
    {
        [Header("Baking Settings")]
        private string _savePath = "Assets/Optimized-Feature/BakedAssets/";
        private int _sampleFrameRate = 30;
        private ShaderPatchMode _shaderPatchMode = ShaderPatchMode.AutoPatchIfMissing;
        private VATBakeOutputMode _outputMode = VATBakeOutputMode.Combined;

        [Header("Baking Input Data")]
        private GameObject _targetPrefab;
        private List<SkinnedMeshRenderer> _detectedSkinnedMeshes = new List<SkinnedMeshRenderer>();
        private List<bool> _selectedMeshToggles = new List<bool>(); // Selection toggles for skinned meshes
        private List<Material> _detectedMaterials = new List<Material>();
        private Animator _detectedAnimator;
        private List<AnimationClip> _detectedClips = new List<AnimationClip>();
        private List<bool> _selectedClipToggles = new List<bool>();

        [Header("Baked Results Preview")]
        private Mesh _outputMesh;
        private Texture2D _outputTexture;
        private List<Material> _outputMaterials = new List<Material>();
        private VATAssetDataSO _outputAssetData;

        // UI Foldouts
        private bool _meshesFoldout = true;
        private bool _materialsFoldout = true;
        private bool _clipsFoldout = true;

        private sealed class MeshBakeSource
        {
            public SkinnedMeshRenderer Renderer;
            public Mesh SharedMesh;
            public Matrix4x4 RendererToTarget;
            public int VertexOffset;
            public int VertexCount;
            public bool UsesHierarchyTransform;
        }

        private sealed class BakedOutput
        {
            public Mesh Mesh;
            public Texture2D Texture;
            public List<Material> Materials = new List<Material>();
            public VATAssetDataSO AssetData;
        }

        [MenuItem("Tools/VAT Bake Tool Simulation")]
        public static void OpenWindow()
        {
            GetWindow<VATBakeToolWindow>("VAT Bake Tool");
        }

        private void OnGUI()
        {
            // --- 1. SETTINGS AT THE TOP ---
            EditorGUILayout.LabelField("General Baking Settings", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            _savePath = EditorGUILayout.TextField("Save Path", _savePath);
            _sampleFrameRate = EditorGUILayout.IntField("Sample FPS", _sampleFrameRate);
            _shaderPatchMode = (ShaderPatchMode)EditorGUILayout.EnumPopup("Shader Patch Mode", _shaderPatchMode);
            _outputMode = (VATBakeOutputMode)EditorGUILayout.EnumPopup("VAT Output Mode", _outputMode);
            if (_outputMode == VATBakeOutputMode.PerSkinnedMesh)
            {
                EditorGUILayout.HelpBox(
                    "Each selected SkinnedMeshRenderer contributes its own material slots/submeshes. " +
                    "All slots still share one static mesh, one VAT texture and one VATAssetDataSO.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "All selected SkinnedMeshRenderers are baked into one VAT data set. " +
                    "Every material slot must use the same Shader and BaseTexture.",
                    MessageType.Info);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space();

            // --- 2. BAKING INPUT DATA ---
            EditorGUILayout.LabelField("Baking Input Data", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            
            EditorGUI.BeginChangeCheck();
            _targetPrefab = (GameObject)EditorGUILayout.ObjectField("Target GameObject", _targetPrefab, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
            {
                LoadBakeReferences();
            }

            if (_targetPrefab != null)
            {
                // Display detected SkinnedMeshRenderers with optional selection toggles
                // Sync toggles count to match detected meshes
                while (_selectedMeshToggles.Count < _detectedSkinnedMeshes.Count)
                    _selectedMeshToggles.Add(true);
                while (_selectedMeshToggles.Count > _detectedSkinnedMeshes.Count)
                    _selectedMeshToggles.RemoveAt(_selectedMeshToggles.Count - 1);

                int activeMeshCount = 0;
                for (int i = 0; i < _selectedMeshToggles.Count; i++)
                    if (_selectedMeshToggles[i]) activeMeshCount++;

                _meshesFoldout = EditorGUILayout.Foldout(_meshesFoldout, $"Detected Skinned Meshes ({_detectedSkinnedMeshes.Count}) — {activeMeshCount} selected for baking");
                if (_meshesFoldout)
                {
                    EditorGUI.indentLevel++;
                    for (int i = 0; i < _detectedSkinnedMeshes.Count; i++)
                    {
                        EditorGUILayout.BeginHorizontal();
                        _selectedMeshToggles[i] = EditorGUILayout.Toggle(_selectedMeshToggles[i], GUILayout.Width(20));
                        EditorGUI.BeginDisabledGroup(!_selectedMeshToggles[i]);
                        EditorGUILayout.ObjectField($"Mesh [{i}]", _detectedSkinnedMeshes[i], typeof(SkinnedMeshRenderer), true);
                        EditorGUI.EndDisabledGroup();
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.indentLevel--;
                }

                // Display detected Materials and Shaders
                _materialsFoldout = EditorGUILayout.Foldout(_materialsFoldout, $"Detected Materials & Shaders ({_detectedMaterials.Count})");
                if (_materialsFoldout)
                {
                    EditorGUI.indentLevel++;
                    for (int i = 0; i < _detectedMaterials.Count; i++)
                    {
                        Material mat = _detectedMaterials[i];
                        string shaderName = mat != null && mat.shader != null ? mat.shader.name : "None";
                        bool hasVAT = mat != null && mat.HasProperty("_VATTex");
                        string status = hasVAT ? "[VAT Ready]" : "[No VAT Code]";
                        EditorGUILayout.LabelField($"Mat [{i}]: {mat.name} -> {shaderName} {status}");
                    }
                    EditorGUI.indentLevel--;
                }

                // Display detected Animator and controller clips with ignore toggles
                _detectedAnimator = (Animator)EditorGUILayout.ObjectField("Detected Animator", _detectedAnimator, typeof(Animator), true);
                _clipsFoldout = EditorGUILayout.Foldout(_clipsFoldout, $"Select Animation Clips to Bake ({_detectedClips.Count})");
                if (_clipsFoldout)
                {
                    EditorGUI.indentLevel++;
                    if (_detectedClips.Count == 0)
                    {
                        EditorGUILayout.HelpBox(
                            "No AnimationClip was found on the detected Animator controller.",
                            MessageType.Info);
                    }

                    for (int i = 0; i < _detectedClips.Count; i++)
                    {
                        EditorGUILayout.BeginHorizontal();
                        _selectedClipToggles[i] = EditorGUILayout.Toggle(
                            _selectedClipToggles[i],
                            GUILayout.Width(20));
                        EditorGUILayout.ObjectField(
                            $"Clip [{i}]",
                            _detectedClips[i],
                            typeof(AnimationClip),
                            false);
                        if (!_selectedClipToggles[i])
                        {
                            EditorGUILayout.LabelField("Ignored", GUILayout.Width(55));
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.indentLevel--;
                }

            }
            else
            {
                EditorGUILayout.HelpBox("Please drag a target character GameObject to load reference inputs.", MessageType.Info);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space();

            // --- 3. BAKED RESULTS PREVIEW ---
            EditorGUILayout.LabelField("Baked Results Preview", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            _outputMesh = (Mesh)EditorGUILayout.ObjectField("Baked Static Mesh", _outputMesh, typeof(Mesh), false);
            _outputTexture = (Texture2D)EditorGUILayout.ObjectField("Baked VAT Texture", _outputTexture, typeof(Texture2D), false);
            
            // Render list of generated materials
            for (int i = 0; i < _outputMaterials.Count; i++)
            {
                _outputMaterials[i] = (Material)EditorGUILayout.ObjectField($"Baked Material [{i}]", _outputMaterials[i], typeof(Material), false);
            }

            _outputAssetData = (VATAssetDataSO)EditorGUILayout.ObjectField(
                "VAT Asset Data SO",
                _outputAssetData,
                typeof(VATAssetDataSO),
                false);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space();

            // --- 4. BAKE BUTTON AT THE BOTTOM ---
            // Compute active mesh count for bake button disabled condition
            int _activeMeshCountForButton = 0;
            for (int i = 0; i < _selectedMeshToggles.Count; i++)
                if (_selectedMeshToggles[i]) _activeMeshCountForButton++;
            EditorGUI.BeginDisabledGroup(_targetPrefab == null || _activeMeshCountForButton == 0);

            // Button label hints that a dialog will appear if existing SO data is detected
            string buttonLabel = _outputAssetData != null ? "Bake VAT Assets..." : "Simulate VAT Baking Pipeline";

            if (GUILayout.Button(buttonLabel, GUILayout.Height(30)))
            {
                BakeVATSimulation();
            }
            EditorGUI.EndDisabledGroup();
        }

        private void LoadBakeReferences()
        {
            _detectedSkinnedMeshes.Clear();
            _selectedMeshToggles.Clear();
            _detectedMaterials.Clear();
            _detectedClips.Clear();
            _selectedClipToggles.Clear();
            _detectedAnimator = null;

            if (_targetPrefab == null) return;

            // Find SkinnedMeshRenderers
            SkinnedMeshRenderer[] smrs = _targetPrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            _detectedSkinnedMeshes.AddRange(smrs);
            // Default all skinned meshes as selected (checked)
            for (int i = 0; i < smrs.Length; i++)
            {
                _selectedMeshToggles.Add(true);
            }

            // Fetch materials and shaders
            foreach (SkinnedMeshRenderer smr in smrs)
            {
                if (smr.sharedMaterials != null)
                {
                    foreach (Material mat in smr.sharedMaterials)
                    {
                        if (mat != null && !_detectedMaterials.Contains(mat))
                        {
                            _detectedMaterials.Add(mat);
                        }
                    }
                }
            }

            // Find Animator and automatically collect its controller clips.
            _detectedAnimator = _targetPrefab.GetComponentInChildren<Animator>(true);
            DetectAnimationClips();

        }

        private void DetectAnimationClips()
        {
            _detectedClips.Clear();
            _selectedClipToggles.Clear();

            if (_detectedAnimator == null || _detectedAnimator.runtimeAnimatorController == null)
            {
                return;
            }

            AnimationClip[] controllerClips = _detectedAnimator.runtimeAnimatorController.animationClips;
            for (int i = 0; i < controllerClips.Length; i++)
            {
                AnimationClip clip = controllerClips[i];
                if (clip != null && !_detectedClips.Contains(clip))
                {
                    _detectedClips.Add(clip);
                    _selectedClipToggles.Add(true);
                }
            }
        }

        private void BakeVATSimulation()
        {
            if (_targetPrefab == null || _detectedSkinnedMeshes.Count == 0) return;

            // Build active meshes list based on selection toggles
            List<SkinnedMeshRenderer> activeMeshes = new List<SkinnedMeshRenderer>();
            for (int i = 0; i < _detectedSkinnedMeshes.Count; i++)
            {
                if (i < _selectedMeshToggles.Count && _selectedMeshToggles[i] && _detectedSkinnedMeshes[i] != null)
                {
                    activeMeshes.Add(_detectedSkinnedMeshes[i]);
                }
            }

            if (activeMeshes.Count == 0)
            {
                Debug.LogError("[VATBakeTool] No Skinned Mesh selected for baking! Please tick at least one mesh in Detected Skinned Meshes.");
                return;
            }

            // Filter detected animation clips using the per-clip ignore toggles.
            List<AnimationClip> clipsToBake = new List<AnimationClip>();
            for (int i = 0; i < _detectedClips.Count; i++)
            {
                if (i < _selectedClipToggles.Count && _selectedClipToggles[i])
                {
                    AnimationClip clip = _detectedClips[i];
                    if (clip != null && !clipsToBake.Contains(clip))
                    {
                        clipsToBake.Add(clip);
                    }
                }
            }

            if (clipsToBake.Count == 0)
            {
                Debug.LogError("[VATBakeTool] No animation clips selected for baking!");
                return;
            }

            bool cancelled;
            bool overrideMode = ResolveOverrideMode(out cancelled);
            if (cancelled) return;

            if (!Directory.Exists(_savePath))
            {
                Directory.CreateDirectory(_savePath);
            }

            // Both output modes produce one VATAssetDataSO and one VAT texture.
            // The mode only controls material validation: PerSkinnedMesh allows
            // each source renderer/material slot to retain its own source shader
            // and BaseTexture; Combined requires those inputs to match.
            bool requireUnifiedShaderAndBaseTexture = _outputMode == VATBakeOutputMode.Combined;
            List<Material> materialSlots;
            string validationMessage;
            if (!CollectMaterialSlots(
                    activeMeshes,
                    requireUnifiedShaderAndBaseTexture,
                    out materialSlots,
                    out validationMessage))
            {
                Debug.LogError($"[VATBakeTool] {validationMessage}");
                EditorUtility.DisplayDialog("VAT Bake Stopped: Material Validation", validationMessage, "OK");
                return;
            }

            BakedOutput output = BakeOutput(
                activeMeshes,
                materialSlots,
                clipsToBake,
                _targetPrefab.name + "_VAT",
                _outputAssetData,
                overrideMode);

            if (output != null)
            {
                _outputMesh = output.Mesh;
                _outputTexture = output.Texture;
                _outputMaterials = output.Materials;
                _outputAssetData = output.AssetData;
            }
        }

        private BakedOutput BakeOutput(
            List<SkinnedMeshRenderer> renderers,
            List<Material> materialSlots,
            List<AnimationClip> clipsToBake,
            string outputName,
            VATAssetDataSO existingAsset,
            bool overrideMode)
        {
            if (renderers == null || renderers.Count == 0 || materialSlots == null || materialSlots.Count == 0)
            {
                return null;
            }

            Shader vatShader = Shader.Find("OptimizedFeature/VAT_Unlit_Luna");
            if (vatShader == null)
            {
                Debug.LogError("[VATBakeTool] Could not find shader 'OptimizedFeature/VAT_Unlit_Luna'.");
                return null;
            }

            HashSet<Material> patchedMaterials = new HashSet<Material>();
            for (int i = 0; i < materialSlots.Count; i++)
            {
                Material material = materialSlots[i];
                if (material != null && patchedMaterials.Add(material))
                {
                    ValidateAndPatchMaterialShader(material);
                }
            }

            List<MeshBakeSource> sources = BuildMeshBakeSources(renderers, materialSlots);
            if (sources.Count == 0)
            {
                Debug.LogError($"[VATBakeTool] No valid mesh source found for '{outputName}'.");
                return null;
            }

            Mesh bakedMesh = BuildCombinedStaticMesh(sources);
            bakedMesh.name = outputName + "_Static";
            int vertexCount = bakedMesh.vertexCount;
            if (vertexCount == 0)
            {
                DestroyImmediate(bakedMesh);
                Debug.LogError($"[VATBakeTool] Mesh '{outputName}' has no vertices.");
                return null;
            }

            Vector2[] uv2 = new Vector2[vertexCount];
            Color[] colors = new Color[vertexCount];
            Vector3[] staticVertices = bakedMesh.vertices;
            float minY = float.MaxValue;
            float maxY = float.MinValue;
            for (int i = 0; i < staticVertices.Length; i++)
            {
                minY = Mathf.Min(minY, staticVertices[i].y);
                maxY = Mathf.Max(maxY, staticVertices[i].y);
            }

            float midY = (minY + maxY) * 0.5f;
            for (int i = 0; i < vertexCount; i++)
            {
                uv2[i] = new Vector2(i, 0f);
                float mask = staticVertices[i].y >= midY ? 1f : 0f;
                colors[i] = new Color(mask, 0f, 0f, 1f);
            }
            bakedMesh.uv2 = uv2;
            bakedMesh.colors = colors;

            Mesh outputMesh = SaveMeshAsset(bakedMesh, outputName, existingAsset, overrideMode);

            List<int> clipFramesList = new List<int>();
            int sampleRate = Mathf.Max(1, _sampleFrameRate);
            int totalBakeFrames = 0;
            for (int i = 0; i < clipsToBake.Count; i++)
            {
                int frames = Mathf.Max(1, Mathf.RoundToInt(clipsToBake[i].length * sampleRate));
                clipFramesList.Add(frames);
                totalBakeFrames += frames;
            }

            Vector3 boundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 boundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            Vector3[] frameVertices = new Vector3[vertexCount];
            Vector3[] firstFrameVertices = new Vector3[vertexCount];
            bool hasFirstFrame = false;
            float maxFrameMotion = 0f;
            GameObject animationSampleRoot = _detectedAnimator != null
                ? _detectedAnimator.gameObject
                : _targetPrefab;
            bool animatorWasEnabled = _detectedAnimator != null && _detectedAnimator.enabled;
            Vector3 boundsSize = Vector3.one;
            Texture2D vatTexture = null;
            if (_detectedAnimator != null)
            {
                _detectedAnimator.enabled = false;
            }

            bool startedAnimationMode = false;
            bool samplingStarted = false;
            try
            {
                if (!AnimationMode.InAnimationMode())
                {
                    AnimationMode.StartAnimationMode();
                    startedAnimationMode = true;
                }

                AnimationMode.BeginSampling();
                samplingStarted = true;

                for (int clipIndex = 0; clipIndex < clipsToBake.Count; clipIndex++)
                {
                    AnimationClip clip = clipsToBake[clipIndex];
                    int framesCount = clipFramesList[clipIndex];
                    for (int frame = 0; frame < framesCount; frame++)
                    {
                        float time = framesCount > 1
                            ? (float)frame / (framesCount - 1) * clip.length
                            : 0f;
                        AnimationMode.SampleAnimationClip(animationSampleRoot, clip, time);
                        BakeFrameVertices(sources, frameVertices);

                        if (!hasFirstFrame)
                        {
                            Array.Copy(frameVertices, firstFrameVertices, vertexCount);
                            hasFirstFrame = true;
                        }
                        else
                        {
                            for (int vertex = 0; vertex < vertexCount; vertex++)
                            {
                                maxFrameMotion = Mathf.Max(
                                    maxFrameMotion,
                                    Vector3.Distance(firstFrameVertices[vertex], frameVertices[vertex]));
                            }
                        }

                        for (int vertex = 0; vertex < frameVertices.Length; vertex++)
                        {
                            boundsMin = Vector3.Min(boundsMin, frameVertices[vertex]);
                            boundsMax = Vector3.Max(boundsMax, frameVertices[vertex]);
                        }
                    }
                }

                Vector3 padding = (boundsMax - boundsMin) * 0.03f;
                boundsMin -= padding;
                boundsMax += padding;
                boundsSize = boundsMax - boundsMin;
                if (boundsSize.x <= 0f) boundsSize.x = 1f;
                if (boundsSize.y <= 0f) boundsSize.y = 1f;
                if (boundsSize.z <= 0f) boundsSize.z = 1f;

                vatTexture = new Texture2D(vertexCount, totalBakeFrames, TextureFormat.RGBAHalf, false);
                vatTexture.name = outputName + "_Texture";
                int globalFrame = 0;

                for (int clipIndex = 0; clipIndex < clipsToBake.Count; clipIndex++)
                {
                    AnimationClip clip = clipsToBake[clipIndex];
                    int framesCount = clipFramesList[clipIndex];
                    for (int frame = 0; frame < framesCount; frame++)
                    {
                        float time = framesCount > 1
                            ? (float)frame / (framesCount - 1) * clip.length
                            : 0f;
                        AnimationMode.SampleAnimationClip(animationSampleRoot, clip, time);
                        BakeFrameVertices(sources, frameVertices);

                        for (int vertex = 0; vertex < vertexCount; vertex++)
                        {
                            Vector3 position = frameVertices[vertex];
                            vatTexture.SetPixel(vertex, globalFrame, new Color(
                                Mathf.Clamp01((position.x - boundsMin.x) / boundsSize.x),
                                Mathf.Clamp01((position.y - boundsMin.y) / boundsSize.y),
                                Mathf.Clamp01((position.z - boundsMin.z) / boundsSize.z),
                                1f));
                        }

                        globalFrame++;
                    }
                }
            }
            finally
            {
                try
                {
                    if (samplingStarted)
                    {
                        AnimationMode.EndSampling();
                    }
                }
                finally
                {
                    if (startedAnimationMode && AnimationMode.InAnimationMode())
                    {
                        AnimationMode.StopAnimationMode();
                    }

                    if (_detectedAnimator != null)
                    {
                        _detectedAnimator.enabled = animatorWasEnabled;
                        _detectedAnimator.Rebind();
                        _detectedAnimator.Update(0f);
                    }
                }
            }

            if (maxFrameMotion <= 0.00001f)
            {
                Debug.LogWarning(
                    $"[VATBakeTool] No vertex motion was detected while sampling '{outputName}'. " +
                    $"Animation root: '{animationSampleRoot.name}'. Check that the selected clips bind to this hierarchy.");
            }

            vatTexture.Apply();

            string textureAssetPath;
            if (overrideMode && existingAsset != null && existingAsset.VATTexture != null)
            {
                textureAssetPath = AssetDatabase.GetAssetPath(existingAsset.VATTexture);
            }
            else
            {
                textureAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.Combine(_savePath, vatTexture.name + ".png").Replace('\\', '/'));
            }

            if (string.IsNullOrEmpty(textureAssetPath))
            {
                DestroyImmediate(vatTexture);
                Debug.LogError($"[VATBakeTool] Could not resolve texture path for '{outputName}'.");
                return null;
            }

            File.WriteAllBytes(textureAssetPath, vatTexture.EncodeToPNG());
            AssetDatabase.ImportAsset(textureAssetPath, ImportAssetOptions.ForceUpdate);
            ConfigureVATTextureImporter(textureAssetPath);
            RegisterAssetInLunaJson(textureAssetPath);

            BakedOutput output = new BakedOutput
            {
                Mesh = outputMesh,
                Texture = AssetDatabase.LoadAssetAtPath<Texture2D>(textureAssetPath)
            };
            DestroyImmediate(vatTexture);

            output.Materials = SaveBakedMaterials(
                materialSlots,
                output.Texture,
                boundsMin,
                boundsMax,
                totalBakeFrames,
                vertexCount,
                outputName,
                existingAsset,
                overrideMode,
                vatShader);

            if (output.Texture == null || output.Materials.Count != materialSlots.Count)
            {
                Debug.LogError($"[VATBakeTool] Failed to create all VAT output assets for '{outputName}'.");
                return null;
            }

            output.AssetData = SaveVATAssetData(
                outputMesh,
                output.Texture,
                output.Materials,
                boundsMin,
                boundsMax,
                totalBakeFrames,
                vertexCount,
                clipsToBake,
                clipFramesList,
                outputName,
                existingAsset,
                overrideMode);

            AssetDatabase.SaveAssets();
            return output;
        }

        private List<MeshBakeSource> BuildMeshBakeSources(
            List<SkinnedMeshRenderer> renderers,
            List<Material> materialSlots)
        {
            List<MeshBakeSource> sources = new List<MeshBakeSource>();
            int vertexOffset = 0;

            for (int i = 0; i < renderers.Count; i++)
            {
                SkinnedMeshRenderer renderer = renderers[i];
                if (renderer == null || renderer.sharedMesh == null) continue;

                Mesh sharedMesh = renderer.sharedMesh;
                MeshBakeSource source = new MeshBakeSource
                {
                    Renderer = renderer,
                    SharedMesh = sharedMesh,
                    RendererToTarget = GetRendererToTargetMatrix(renderer),
                    VertexOffset = vertexOffset,
                    VertexCount = sharedMesh.vertexCount,
                    // A renderer with valid skinning data must resolve its own
                    // bindposes/boneWeights against the current bone matrices,
                    // including when its GameObject is placed below a bone. The
                    // frame path writes directly in target-root space.
                    // Only a mesh without skinning data is driven by its hierarchy.
                    UsesHierarchyTransform = !HasSkinningData(renderer, sharedMesh)
                };

                sources.Add(source);
                vertexOffset += source.VertexCount;
            }

            int expectedSubmeshCount = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                expectedSubmeshCount += Mathf.Max(1, sources[i].SharedMesh.subMeshCount);
            }

            if (expectedSubmeshCount != materialSlots.Count)
            {
                Debug.LogError(
                    $"[VATBakeTool] Internal material/submesh mapping mismatch: " +
                    $"{expectedSubmeshCount} submeshes, {materialSlots.Count} materials.");
            }

            return sources;
        }

        private Mesh BuildCombinedStaticMesh(List<MeshBakeSource> sources)
        {
            int totalVertexCount = 0;
            int totalSubmeshCount = 0;
            bool hasNormals = true;
            bool hasTangents = true;

            for (int i = 0; i < sources.Count; i++)
            {
                Mesh sourceMesh = sources[i].SharedMesh;
                totalVertexCount += sourceMesh.vertexCount;
                totalSubmeshCount += Mathf.Max(1, sourceMesh.subMeshCount);
                hasNormals &= sourceMesh.normals != null && sourceMesh.normals.Length == sourceMesh.vertexCount;
                hasTangents &= sourceMesh.tangents != null && sourceMesh.tangents.Length == sourceMesh.vertexCount;
            }

            Mesh combinedMesh = new Mesh
            {
                indexFormat = totalVertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                subMeshCount = totalSubmeshCount
            };

            List<Vector3> vertices = new List<Vector3>(totalVertexCount);
            List<Vector3> normals = hasNormals ? new List<Vector3>(totalVertexCount) : null;
            List<Vector4> tangents = hasTangents ? new List<Vector4>(totalVertexCount) : null;
            List<Vector2> uv = new List<Vector2>(totalVertexCount);
            List<int[]> submeshIndices = new List<int[]>(totalSubmeshCount);
            List<MeshTopology> submeshTopologies = new List<MeshTopology>(totalSubmeshCount);
            int outputSubmeshIndex = 0;

            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                MeshBakeSource source = sources[sourceIndex];
                Mesh sourceMesh = source.SharedMesh;
                Vector3[] sourceVertices = sourceMesh.vertices;
                Vector3[] sourceNormals = hasNormals ? sourceMesh.normals : null;
                Vector4[] sourceTangents = hasTangents ? sourceMesh.tangents : null;
                Vector2[] sourceUv = sourceMesh.uv;
                Matrix4x4 normalMatrix = source.RendererToTarget.inverse.transpose;

                for (int vertexIndex = 0; vertexIndex < sourceVertices.Length; vertexIndex++)
                {
                    vertices.Add(source.RendererToTarget.MultiplyPoint3x4(sourceVertices[vertexIndex]));

                    if (normals != null)
                    {
                        Vector3 normal = normalMatrix.MultiplyVector(sourceNormals[vertexIndex]);
                        normals.Add(normal.sqrMagnitude > 0f ? normal.normalized : Vector3.up);
                    }

                    if (tangents != null)
                    {
                        Vector4 tangent = sourceTangents[vertexIndex];
                        Vector3 tangentDirection = source.RendererToTarget.MultiplyVector(
                            new Vector3(tangent.x, tangent.y, tangent.z));
                        tangentDirection = tangentDirection.sqrMagnitude > 0f
                            ? tangentDirection.normalized
                            : Vector3.right;
                        tangents.Add(new Vector4(tangentDirection.x, tangentDirection.y, tangentDirection.z, tangent.w));
                    }

                    uv.Add(vertexIndex < sourceUv.Length ? sourceUv[vertexIndex] : Vector2.zero);
                }

                int submeshCount = Mathf.Max(1, sourceMesh.subMeshCount);
                for (int submeshIndex = 0; submeshIndex < submeshCount; submeshIndex++)
                {
                    int[] sourceIndices = sourceMesh.GetIndices(submeshIndex);
                    int[] outputIndices = new int[sourceIndices.Length];
                    for (int index = 0; index < sourceIndices.Length; index++)
                    {
                        outputIndices[index] = sourceIndices[index] + source.VertexOffset;
                    }

                    submeshIndices.Add(outputIndices);
                    submeshTopologies.Add(sourceMesh.GetTopology(submeshIndex));
                    outputSubmeshIndex++;
                }
            }

            combinedMesh.SetVertices(vertices);
            if (normals != null) combinedMesh.SetNormals(normals);
            if (tangents != null) combinedMesh.SetTangents(tangents);
            combinedMesh.SetUVs(0, uv);
            for (int i = 0; i < submeshIndices.Count; i++)
            {
                combinedMesh.SetIndices(submeshIndices[i], submeshTopologies[i], i, false);
            }
            combinedMesh.RecalculateBounds();
            return combinedMesh;
        }

        private void BakeFrameVertices(
            List<MeshBakeSource> sources,
            Vector3[] combinedVertices)
        {
            Array.Clear(combinedVertices, 0, combinedVertices.Length);

            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                MeshBakeSource source = sources[sourceIndex];
                // Refresh after SampleAnimation so a renderer under a bone uses the
                // current hierarchy pose when it is not itself skinned.
                source.RendererToTarget = GetRendererToTargetMatrix(source.Renderer);

                Vector3[] sourceVertices;
                if (source.UsesHierarchyTransform)
                {
                    sourceVertices = source.SharedMesh.vertices;

                    int copyCount = Mathf.Min(source.VertexCount, sourceVertices.Length);
                    for (int vertexIndex = 0; vertexIndex < copyCount; vertexIndex++)
                    {
                        combinedVertices[source.VertexOffset + vertexIndex] =
                            source.RendererToTarget.MultiplyPoint3x4(sourceVertices[vertexIndex]);
                    }
                }
                else
                {
                    // Resolve the current pose from the source renderer's own
                    // bone list. This avoids treating the target root as the
                    // owner of a sub-mesh mounted below a bone and keeps the
                    // vertex result independent from BakeMesh's renderer space.
                    BakeSkinnedVerticesInTargetSpace(source, combinedVertices);
                }
            }
        }

        private void BakeSkinnedVerticesInTargetSpace(
            MeshBakeSource source,
            Vector3[] combinedVertices)
        {
            Mesh mesh = source.SharedMesh;
            BoneWeight[] boneWeights = mesh.boneWeights;
            Matrix4x4[] bindposes = mesh.bindposes;
            Transform[] bones = source.Renderer.bones;
            Vector3[] sourceVertices = mesh.vertices;

            if (boneWeights == null || boneWeights.Length != source.VertexCount ||
                bindposes == null || bindposes.Length == 0 || bones == null || bones.Length == 0)
            {
                Debug.LogError(
                    $"[VATBakeTool] Invalid skinning data on '{source.Renderer.name}'. " +
                    "Falling back to the renderer hierarchy for this frame.");

                int fallbackCount = Mathf.Min(source.VertexCount, sourceVertices.Length);
                for (int vertexIndex = 0; vertexIndex < fallbackCount; vertexIndex++)
                {
                    combinedVertices[source.VertexOffset + vertexIndex] =
                        source.RendererToTarget.MultiplyPoint3x4(sourceVertices[vertexIndex]);
                }
                return;
            }

            Matrix4x4 worldToTarget = _targetPrefab.transform.worldToLocalMatrix;
            for (int vertexIndex = 0; vertexIndex < source.VertexCount; vertexIndex++)
            {
                BoneWeight weights = boneWeights[vertexIndex];
                Vector3 targetPosition = Vector3.zero;
                float totalWeight = 0f;

                AddBoneContribution(
                    ref targetPosition,
                    ref totalWeight,
                    sourceVertices[vertexIndex],
                    weights.boneIndex0,
                    weights.weight0,
                    bones,
                    bindposes,
                    worldToTarget);
                AddBoneContribution(
                    ref targetPosition,
                    ref totalWeight,
                    sourceVertices[vertexIndex],
                    weights.boneIndex1,
                    weights.weight1,
                    bones,
                    bindposes,
                    worldToTarget);
                AddBoneContribution(
                    ref targetPosition,
                    ref totalWeight,
                    sourceVertices[vertexIndex],
                    weights.boneIndex2,
                    weights.weight2,
                    bones,
                    bindposes,
                    worldToTarget);
                AddBoneContribution(
                    ref targetPosition,
                    ref totalWeight,
                    sourceVertices[vertexIndex],
                    weights.boneIndex3,
                    weights.weight3,
                    bones,
                    bindposes,
                    worldToTarget);

                if (totalWeight <= Mathf.Epsilon)
                {
                    targetPosition = source.RendererToTarget.MultiplyPoint3x4(sourceVertices[vertexIndex]);
                }

                combinedVertices[source.VertexOffset + vertexIndex] = targetPosition;
            }
        }

        private static void AddBoneContribution(
            ref Vector3 targetPosition,
            ref float totalWeight,
            Vector3 sourceVertex,
            int boneIndex,
            float weight,
            Transform[] bones,
            Matrix4x4[] bindposes,
            Matrix4x4 worldToTarget)
        {
            if (weight <= 0f || boneIndex < 0 || boneIndex >= bones.Length ||
                boneIndex >= bindposes.Length || bones[boneIndex] == null)
            {
                return;
            }

            Matrix4x4 boneToTarget = worldToTarget * bones[boneIndex].localToWorldMatrix * bindposes[boneIndex];
            targetPosition += boneToTarget.MultiplyPoint3x4(sourceVertex) * weight;
            totalWeight += weight;
        }

        private Matrix4x4 GetRendererToTargetMatrix(SkinnedMeshRenderer renderer)
        {
            // Converting world -> target-root local cancels the input GameObject's
            // position/rotation/scale. Runtime can still apply the root transform to
            // the final VAT object, but it cannot distort bake-space vertex positions.
            return _targetPrefab.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
        }

        private static bool HasSkinningData(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            return renderer != null && mesh != null &&
                   renderer.bones != null && renderer.bones.Length > 0 &&
                   mesh.bindposes != null && mesh.bindposes.Length > 0 &&
                   mesh.boneWeights != null && mesh.boneWeights.Length == mesh.vertexCount;
        }

        private bool CollectMaterialSlots(
            List<SkinnedMeshRenderer> renderers,
            bool requireUnifiedShaderAndBaseTexture,
            out List<Material> materialSlots,
            out string validationMessage)
        {
            materialSlots = new List<Material>();
            List<string> errors = new List<string>();
            Shader referenceShader = null;
            Texture referenceBaseTexture = null;
            bool hasReferenceBaseTexture = false;
            HashSet<string> shaderNames = new HashSet<string>();

            for (int rendererIndex = 0; rendererIndex < renderers.Count; rendererIndex++)
            {
                SkinnedMeshRenderer renderer = renderers[rendererIndex];
                if (renderer == null)
                {
                    errors.Add("A selected SkinnedMeshRenderer is null.");
                    continue;
                }

                Mesh sharedMesh = renderer.sharedMesh;
                if (sharedMesh == null)
                {
                    errors.Add($"Renderer '{renderer.name}' has no shared Mesh.");
                    continue;
                }
                if (sharedMesh.subMeshCount <= 0)
                {
                    errors.Add($"Renderer '{renderer.name}' has no submesh data.");
                    continue;
                }

                Material[] sharedMaterials = renderer.sharedMaterials;
                int submeshCount = Mathf.Max(1, sharedMesh.subMeshCount);
                for (int submeshIndex = 0; submeshIndex < submeshCount; submeshIndex++)
                {
                    Material material = sharedMaterials != null && submeshIndex < sharedMaterials.Length
                        ? sharedMaterials[submeshIndex]
                        : null;
                    materialSlots.Add(material);

                    if (material == null)
                    {
                        errors.Add($"Renderer '{renderer.name}' submesh {submeshIndex} has no Material.");
                        continue;
                    }

                    if (material.shader == null)
                    {
                        errors.Add($"Material '{material.name}' has no Shader.");
                        continue;
                    }

                    shaderNames.Add(material.shader.name);
                    if (!requireUnifiedShaderAndBaseTexture) continue;

                    if (referenceShader == null)
                    {
                        referenceShader = material.shader;
                    }
                    else if (referenceShader != material.shader)
                    {
                        errors.Add(
                            $"Shader mismatch on '{renderer.name}' submesh {submeshIndex}: " +
                            $"expected '{referenceShader.name}', found '{material.shader.name}'.");
                    }

                    Texture baseTexture = GetBaseTexture(material);
                    if (!hasReferenceBaseTexture)
                    {
                        referenceBaseTexture = baseTexture;
                        hasReferenceBaseTexture = true;
                    }
                    else if (referenceBaseTexture != baseTexture)
                    {
                        errors.Add(
                            $"BaseTexture mismatch on '{renderer.name}' submesh {submeshIndex}: " +
                            "all combined material slots must reference the same texture.");
                    }
                }
            }

            if (requireUnifiedShaderAndBaseTexture && shaderNames.Count > 1)
            {
                errors.Add("Combined mode requires one shared Shader across all selected material slots.");
            }

            if (errors.Count == 0)
            {
                validationMessage = string.Empty;
                return true;
            }

            validationMessage = "Material validation failed:\n- " + string.Join("\n- ", errors);
            return false;
        }

        private static Texture GetBaseTexture(Material material)
        {
            if (material == null) return null;
            if (material.HasProperty("_BaseMap")) return material.GetTexture("_BaseMap");
            if (material.HasProperty("_MainTex")) return material.GetTexture("_MainTex");
            return null;
        }

        private bool ResolveOverrideMode(out bool cancelled)
        {
            cancelled = false;
            if (!HasExistingOutputAsset()) return false;

            int dialogChoice = EditorUtility.DisplayDialogComplex(
                "VAT Asset Data Already Exists",
                $"The output currently contains '{_outputAssetData.name}'.\n\n" +
                "• Override — Overwrite existing assets in-place (keeps GUIDs and project references).\n" +
                "• New — Create new asset files in the Save Path folder.\n" +
                "• Cancel — Abort.",
                "Override",
                "Cancel",
                "New");

            if (dialogChoice == 1)
            {
                cancelled = true;
                return false;
            }
            return dialogChoice == 0;
        }

        private bool HasExistingOutputAsset()
        {
            return _outputAssetData != null;
        }

        private Mesh SaveMeshAsset(
            Mesh bakedMesh,
            string outputName,
            VATAssetDataSO existingAsset,
            bool overrideMode)
        {
            if (overrideMode && existingAsset != null && existingAsset.BakedStaticMesh != null)
            {
                Mesh existingMesh = existingAsset.BakedStaticMesh;
                CopyMeshData(existingMesh, bakedMesh);
                EditorUtility.SetDirty(existingMesh);
                DestroyImmediate(bakedMesh);
                return existingMesh;
            }

            string meshAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                Path.Combine(_savePath, outputName + "_Static.asset").Replace('\\', '/'));
            AssetDatabase.CreateAsset(bakedMesh, meshAssetPath);
            return AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath);
        }

        private static void CopyMeshData(Mesh destination, Mesh source)
        {
            destination.Clear();

            // The VAT output is a plain static mesh. An older bake may have
            // stored bindposes/boneWeights in this same asset, and Clear()
            // does not make that legacy skinning state safe by itself. Leaving
            // it behind makes Unity validate stale bone indices during
            // AssetDatabase.SaveAssets().
            destination.bindposes = new Matrix4x4[0];
            destination.boneWeights = new BoneWeight[0];

            destination.indexFormat = source.indexFormat;
            destination.vertices = source.vertices;
            if (source.normals != null && source.normals.Length == source.vertexCount)
            {
                destination.normals = source.normals;
            }
            if (source.tangents != null && source.tangents.Length == source.vertexCount)
            {
                destination.tangents = source.tangents;
            }
            destination.uv = source.uv;
            destination.uv2 = source.uv2;
            destination.colors = source.colors;
            destination.subMeshCount = source.subMeshCount;
            for (int i = 0; i < source.subMeshCount; i++)
            {
                destination.SetIndices(source.GetIndices(i), source.GetTopology(i), i, false);
            }
            destination.RecalculateBounds();
        }

        private static void ConfigureVATTextureImporter(string textureAssetPath)
        {
            TextureImporter importer = AssetImporter.GetAtPath(textureAssetPath) as TextureImporter;
            if (importer == null) return;

            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.filterMode = FilterMode.Point;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = false;
            importer.isReadable = true;
            importer.wrapMode = TextureWrapMode.Clamp;

            TextureImporterPlatformSettings defaultSettings = importer.GetDefaultPlatformTextureSettings();
            defaultSettings.overridden = true;
            defaultSettings.format = TextureImporterFormat.RGBA32;
            defaultSettings.textureCompression = TextureImporterCompression.Uncompressed;
            defaultSettings.maxTextureSize = 4096;
            importer.SetPlatformTextureSettings(defaultSettings);

            TextureImporterPlatformSettings webglSettings = importer.GetPlatformTextureSettings("WebGL");
            webglSettings.overridden = true;
            webglSettings.format = TextureImporterFormat.RGBA32;
            webglSettings.textureCompression = TextureImporterCompression.Uncompressed;
            webglSettings.maxTextureSize = 4096;
            importer.SetPlatformTextureSettings(webglSettings);
            importer.SaveAndReimport();
        }

        private List<Material> SaveBakedMaterials(
            List<Material> materialSlots,
            Texture2D vatTexture,
            Vector3 boundsMin,
            Vector3 boundsMax,
            int totalBakeFrames,
            int vertexCount,
            string outputName,
            VATAssetDataSO existingAsset,
            bool overrideMode,
            Shader vatShader)
        {
            List<Material> outputMaterials = new List<Material>();
            int vatTextureId = Shader.PropertyToID("_VATTex");
            int boundsMinId = Shader.PropertyToID("_BoundingMin");
            int boundsMaxId = Shader.PropertyToID("_BoundingMax");
            int framesId = Shader.PropertyToID("_NumFrames");
            int verticesId = Shader.PropertyToID("_NumVertices");

            for (int i = 0; i < materialSlots.Count; i++)
            {
                Material originalMaterial = materialSlots[i];
                Material outputMaterial = null;

                if (overrideMode && existingAsset != null &&
                    i < existingAsset.BakedMaterials.Count &&
                    existingAsset.BakedMaterials[i] != null)
                {
                    outputMaterial = existingAsset.BakedMaterials[i];
                    outputMaterial.shader = vatShader;
                    outputMaterial.SetTexture(vatTextureId, vatTexture);
                    outputMaterial.SetVector(boundsMinId, boundsMin);
                    outputMaterial.SetVector(boundsMaxId, boundsMax);
                    outputMaterial.SetFloat(framesId, totalBakeFrames);
                    outputMaterial.SetFloat(verticesId, vertexCount);
                    EditorUtility.SetDirty(outputMaterial);
                }
                else
                {
                    outputMaterial = Instantiate(originalMaterial);
                    string materialSuffix = materialSlots.Count > 1 ? "_" + i : string.Empty;
                    outputMaterial.name = originalMaterial.name + "_VAT" + materialSuffix;
                    outputMaterial.shader = vatShader;
                    outputMaterial.SetTexture(vatTextureId, vatTexture);
                    outputMaterial.SetVector(boundsMinId, boundsMin);
                    outputMaterial.SetVector(boundsMaxId, boundsMax);
                    outputMaterial.SetFloat(framesId, totalBakeFrames);
                    outputMaterial.SetFloat(verticesId, vertexCount);

                    string materialAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                        Path.Combine(_savePath, outputMaterial.name + ".mat").Replace('\\', '/'));
                    AssetDatabase.CreateAsset(outputMaterial, materialAssetPath);
                    outputMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialAssetPath);
                }

                outputMaterials.Add(outputMaterial);
            }

            return outputMaterials;
        }

        private VATAssetDataSO SaveVATAssetData(
            Mesh outputMesh,
            Texture2D outputTexture,
            List<Material> outputMaterials,
            Vector3 boundsMin,
            Vector3 boundsMax,
            int totalBakeFrames,
            int vertexCount,
            List<AnimationClip> clipsToBake,
            List<int> clipFramesList,
            string outputName,
            VATAssetDataSO existingAsset,
            bool overrideMode)
        {
            VATAssetDataSO assetData = existingAsset;
            if (overrideMode && assetData != null)
            {
                assetData.BakedStaticMesh = outputMesh;
                assetData.VATTexture = outputTexture;
                assetData.BakedMaterials.Clear();
                assetData.BakedMaterials.AddRange(outputMaterials);
                assetData.TotalVertices = vertexCount;
                assetData.TotalFrames = totalBakeFrames;
                assetData.BoundingMin = boundsMin;
                assetData.BoundingMax = boundsMax;
            }
            else
            {
                assetData = ScriptableObject.CreateInstance<VATAssetDataSO>();
                assetData.BakedStaticMesh = outputMesh;
                assetData.VATTexture = outputTexture;
                assetData.BakedMaterials.AddRange(outputMaterials);
                assetData.TotalVertices = vertexCount;
                assetData.TotalFrames = totalBakeFrames;
                assetData.BoundingMin = boundsMin;
                assetData.BoundingMax = boundsMax;

                string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.Combine(_savePath, outputName + "Data.asset").Replace('\\', '/'));
                AssetDatabase.CreateAsset(assetData, assetPath);
            }

            assetData.Clips.Clear();
            int startFrame = 0;
            int sampleRate = Mathf.Max(1, _sampleFrameRate);
            for (int i = 0; i < clipsToBake.Count; i++)
            {
                int endFrame = startFrame + clipFramesList[i] - 1;
                assetData.Clips.Add(new VATClipInfo
                {
                    ClipName = clipsToBake[i].name,
                    StateHash = VATClipInfo.GenerateHash(clipsToBake[i].name),
                    StartFrame = startFrame,
                    EndFrame = endFrame,
                    FrameRate = sampleRate
                });
                startFrame = endFrame + 1;
            }

            // Socket baking for VAT_ObjectMesh is intentionally disabled. Clear
            // legacy data when overriding an older VAT asset so the output stays
            // limited to mesh animation data.
            assetData.Sockets.Clear();
            EditorUtility.SetDirty(assetData);
            return assetData;
        }

        private void ValidateAndPatchMaterialShader(Material mat)
        {
            if (mat == null || mat.shader == null) return;
            bool hasVATProperty = mat.HasProperty("_VATTex");

            if (_shaderPatchMode == ShaderPatchMode.AlwaysForcePatch || (!hasVATProperty && _shaderPatchMode == ShaderPatchMode.AutoPatchIfMissing))
            {
                Debug.LogWarning($"[VATBakeTool] Shader '{mat.shader.name}' on Material '{mat.name}' is missing VAT support. Auto-patching...");
            }
        }

        private bool AreAssetsAlreadyBaked()
        {
            if (_targetPrefab == null) return false;

            string meshPath = Path.Combine(_savePath, _targetPrefab.name + "_VAT_Static.asset");
            string texPath = Path.Combine(_savePath, _targetPrefab.name + "_VAT_Texture.png");
            string soPath = Path.Combine(_savePath, _targetPrefab.name + "_VATData.asset");

            return File.Exists(meshPath) || File.Exists(texPath) || File.Exists(soPath);
        }

        private static void RegisterAssetInLunaJson(string assetPath)
        {
            string lunaJsonPath = Path.Combine(Directory.GetCurrentDirectory(), "luna.json");
            if (!File.Exists(lunaJsonPath)) return;

            try
            {
                string jsonText = File.ReadAllText(lunaJsonPath);
                string normalizedPath = assetPath.Replace('\\', '/');
                if (!jsonText.Contains($"\"{normalizedPath}\""))
                {
                    int includesIndex = jsonText.IndexOf("\"includes\": [");
                    if (includesIndex != -1)
                    {
                        int insertPos = jsonText.IndexOf('[', includesIndex) + 1;
                        string entry = $"\n                \"{normalizedPath}\",";
                        jsonText = jsonText.Insert(insertPos, entry);
                        File.WriteAllText(lunaJsonPath, jsonText);
                        Debug.Log($"[VATBakeTool] Auto-registered '{normalizedPath}' into luna.json asset includes list.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VATBakeTool] Failed to auto-register asset in luna.json: {ex.Message}");
            }
        }
    }
}
