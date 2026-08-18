using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

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
        // Controls material validation within each Body/Weapon VAT channel.
        PerSkinnedMesh,
        Combined
    }

    public enum VATRendererRole
    {
        Body,
        Weapon
    }

    /// <summary>
    /// Editor Tool Window implementing the VAT Bake process with automatic references discovery,
    /// selective animation clip baking and Material/Shader validation displays.
    /// </summary>
    public class VATBakeToolWindow : EditorWindow
    {
        // VAT data uses one texel per vertex/frame and importer settings target
        // 4096 for both Default and WebGL. Exceeding either dimension would
        // resize the asset and invalidate the vertex-to-texel mapping.
        private const int MaxVATTextureDimension = 4096;
        private const string LunaVATTextureFormat = "png32";
        private const string LunaVATTextureCompression = "none";
        private const int LunaVATTextureQuality = 100;
        private const float SectionTabHeight = 24f;
        private const float CommonFooterHeight = 210f;

        [Header("Baking Settings")]
        private string _savePath = "Assets/Optimized-Feature/BakedAssets/";
        private int _sampleFrameRate = 30;
        private ShaderPatchMode _shaderPatchMode = ShaderPatchMode.AutoPatchIfMissing;
        private VATBakeOutputMode _outputMode = VATBakeOutputMode.PerSkinnedMesh;

        [Header("Baking Input Data")]
        private GameObject _targetPrefab;
        private string _outputName;
        private List<SkinnedMeshRenderer> _detectedSkinnedMeshes = new List<SkinnedMeshRenderer>();
        private List<bool> _selectedMeshToggles = new List<bool>(); // Selection toggles for skinned meshes
        private List<VATRendererRole> _meshRoles = new List<VATRendererRole>();
        private List<string> _weaponOutputNames = new List<string>();
        private List<Material> _detectedMaterials = new List<Material>();
        private Animator _detectedAnimator;
        private List<AnimationClip> _controllerClips = new List<AnimationClip>();
        private List<AnimationClip> _animationMergeOutputs = new List<AnimationClip>();
        private List<AnimationClip> _detectedClips = new List<AnimationClip>();
        private List<bool> _selectedClipToggles = new List<bool>();
        private bool _usingAnimationMergeOutputs;
        private bool _embeddedAnimationMergeGraphLoaded;
        private AnimationMergeGraphWindow.EmbeddedGraphHandle _embeddedAnimationMergeGraph;
        private VisualElement _embeddedAnimationMergeSurface;

        [Header("Baked Output Source Of Truth")]
        private VATAssetDataSO _outputAssetData;
        private string _activeOutputDirectory;
        private string _cachedLunaTexturePath;
        private DateTime _cachedLunaJsonWriteTimeUtc = DateTime.MinValue;
        private bool _cachedLunaOverrideFound;
        private LunaTextureOverrideSettings _cachedLunaOverrideSettings;

        [Header("Runtime Setup Data")]
        [SerializeField] private List<GameObject> _setupTargetRoots = new List<GameObject>();
        [SerializeField] private VATAssetDataSO _setupVATAssetData;
        [SerializeField] private Material _setupVATMaterial;
        [SerializeField]
        private List<VATSetupHelper.SocketAttachmentSetup> _setupAttachments =
            new List<VATSetupHelper.SocketAttachmentSetup>();
        private SerializedObject _setupSerializedObject;
        private SerializedProperty _setupTargetRootsProperty;
        private SerializedProperty _setupAttachmentsProperty;

        // UI Navigation / Foldouts
        private static readonly string[] SectionTabs =
        {
            "1. Settings", "2. Inputs", "3. Outputs", "4. Runtime Setup"
        };
        private int _selectedSectionTab;
        private Vector2 _sectionScrollPosition;
        private bool _meshesFoldout = true;
        private bool _materialsFoldout = true;
        private bool _clipsFoldout = true;
        private bool _outputPreviewFoldout = true;
        private bool _vatTextureQualityFoldout = true;
        private bool _materialTextureQualityFoldout = true;

        private sealed class MeshBakeSource
        {
            public SkinnedMeshRenderer Renderer;
            public Mesh SharedMesh;
            public Matrix4x4 RendererToTarget;
            public int VertexOffset;
            public int VertexCount;
            public bool UsesHierarchyTransform;
        }

        private sealed class BakedChannelOutput
        {
            public Mesh Mesh;
            public Texture2D Texture;
            public List<Material> Materials = new List<Material>();
            public Vector3 BoundsMin;
            public Vector3 BoundsMax;
            public int VertexCount;
            public int TotalFrames;
        }

        private sealed class BakedOutput
        {
            public BakedChannelOutput Body;
            public VATAssetDataSO AssetData;
            public List<VATWeaponAssetSO> WeaponAssets = new List<VATWeaponAssetSO>();
        }

        private sealed class WeaponBakeGroup
        {
            public string Name;
            public List<SkinnedMeshRenderer> Renderers = new List<SkinnedMeshRenderer>();
            public List<Material> Materials = new List<Material>();
            public VATWeaponAssetSO ExistingAsset;
            public BakedChannelOutput Channel;
            public VATWeaponAssetSO Asset;
        }

        private struct LunaTextureOverrideSettings
        {
            public bool Exists;
            public int MaxWidth;
            public int MaxHeight;
            public string Format;
            public string Compression;
            public int Quality;
        }

        [MenuItem("Tools/VAT Bake Tool Simulation")]
        public static void OpenWindow()
        {
            VATBakeToolWindow window = GetWindow<VATBakeToolWindow>("VAT Bake Tool");
            window.minSize = new Vector2(480f, 440f);
        }

        private void OnEnable()
        {
            minSize = new Vector2(480f, 440f);
            InitializeSetupSerializedProperties();
        }

        private void OnDisable()
        {
            DisposeEmbeddedAnimationMergeGraph();
        }

        private void CreateGUI()
        {
            IMGUIContainer legacyContent = new IMGUIContainer(DrawWindowGUI)
            {
                name = "VATBakeLegacyContent"
            };
            legacyContent.style.flexGrow = 1f;
            rootVisualElement.Add(legacyContent);
        }

        private void DrawWindowGUI()
        {
            Rect tabRect = new Rect(0f, 0f, position.width, SectionTabHeight);
            int selectedTab = GUI.Toolbar(tabRect, _selectedSectionTab, SectionTabs);
            if (selectedTab != _selectedSectionTab)
            {
                _selectedSectionTab = selectedTab;
                _sectionScrollPosition = Vector2.zero;
            }

            float contentTop = SectionTabHeight + 4f;
            float footerHeight = Mathf.Min(CommonFooterHeight, Mathf.Max(120f, position.height - contentTop));
            Rect contentRect = new Rect(
                0f,
                contentTop,
                position.width,
                Mathf.Max(1f, position.height - contentTop - footerHeight - 4f));
            Rect footerRect = new Rect(
                0f,
                position.height - footerHeight,
                position.width,
                footerHeight);

            GUILayout.BeginArea(contentRect);
            _sectionScrollPosition = EditorGUILayout.BeginScrollView(_sectionScrollPosition);
            switch (_selectedSectionTab)
            {
                case 0:
                    DrawSettingsPage();
                    break;
                case 1:
                    DrawInputsPage();
                    break;
                case 2:
                    DrawOutputsPage();
                    break;
                case 3:
                    DrawVATSetupPage();
                    break;
            }
            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();

            GUILayout.BeginArea(footerRect);
            if (_selectedSectionTab == 3)
            {
                DrawVATSetupFooter();
            }
            else
            {
                DrawCommonFooter();
            }
            GUILayout.EndArea();

            UpdateEmbeddedAnimationMergeSurface(footerHeight);
        }

        private void UpdateEmbeddedAnimationMergeSurface(float footerHeight)
        {
            if (_embeddedAnimationMergeSurface == null)
            {
                return;
            }

            _embeddedAnimationMergeSurface.style.position = Position.Absolute;
            _embeddedAnimationMergeSurface.style.left = 0f;
            _embeddedAnimationMergeSurface.style.right = 0f;
            _embeddedAnimationMergeSurface.style.top = SectionTabHeight + 4f;
            _embeddedAnimationMergeSurface.style.bottom = footerHeight + 4f;
            _embeddedAnimationMergeSurface.style.display =
                _embeddedAnimationMergeGraphLoaded && _selectedSectionTab == 1
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
        }

        private void DisposeEmbeddedAnimationMergeGraph()
        {
            if (_embeddedAnimationMergeGraph != null)
            {
                _embeddedAnimationMergeGraph.Dispose();
                _embeddedAnimationMergeGraph = null;
            }

            _embeddedAnimationMergeSurface = null;
            _embeddedAnimationMergeGraphLoaded = false;
        }

        private void DrawSettingsPage()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Baking Settings", EditorStyles.boldLabel);
            _savePath = EditorGUILayout.TextField("Save Path", _savePath);
            _sampleFrameRate = EditorGUILayout.IntField("Sample FPS", _sampleFrameRate);
            _shaderPatchMode = (ShaderPatchMode)EditorGUILayout.EnumPopup("Shader Patch Mode", _shaderPatchMode);
            _outputMode = (VATBakeOutputMode)EditorGUILayout.EnumPopup("VAT Output Mode", _outputMode);
            if (_outputMode == VATBakeOutputMode.PerSkinnedMesh)
            {
                EditorGUILayout.HelpBox(
                    "Per SkinnedMesh allows different source shaders and base textures. " +
                    "The result uses a Body VAT channel and an optional Weapon VAT sub-render channel.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Combined requires every material slot to share the same Shader and BaseTexture.",
                    MessageType.Info);
            }
            EditorGUILayout.HelpBox(
                "Assign each detected renderer to Body or Weapon. Both channels are sampled from the same clip/frame manifest; Weapon is optional.",
                MessageType.Info);
            EditorGUILayout.HelpBox(
                "VAT textures are protected for Luna with a 4096 x 4096 PNG32, compression-none override.",
                MessageType.Info);
            EditorGUILayout.EndVertical();
        }

        private void DrawInputsPage()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Baking Inputs", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _targetPrefab = (GameObject)EditorGUILayout.ObjectField("Target GameObject", _targetPrefab, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
            {
                LoadBakeReferences();
            }
            _outputName = EditorGUILayout.TextField("Output Name (Optional)", _outputName);
            EditorGUILayout.HelpBox(
                "Leave Output Name empty to use the target GameObject name. All newly created output files are saved in a folder named after the VATAssetDataSO.",
                MessageType.None);

            if (_targetPrefab != null)
            {
                SyncMeshSelectionToggles();
                int activeMeshCount = GetSelectedMeshCount();

                EditorGUILayout.HelpBox(
                    "Each selected renderer is baked into Body or Weapon. Enter a Weapon Name after the role: matching names are combined into one VATWeaponAssetSO, while different names create separate weapon outputs.",
                    MessageType.None);
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
                        _meshRoles[i] = (VATRendererRole)EditorGUILayout.EnumPopup(
                            GUIContent.none,
                            _meshRoles[i],
                            GUILayout.Width(78));
                        if (_meshRoles[i] == VATRendererRole.Weapon)
                        {
                            _weaponOutputNames[i] = EditorGUILayout.TextField(
                                new GUIContent("", "Weapon output name. Renderers with the same name bake into one VATWeaponAssetSO."),
                                _weaponOutputNames[i],
                                GUILayout.Width(120));
                        }
                        EditorGUI.EndDisabledGroup();
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.indentLevel--;
                }

                if (_detectedSkinnedMeshes.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "The selected GameObject has no SkinnedMeshRenderer to bake.",
                        MessageType.Warning);
                }
                else if (activeMeshCount == 0)
                {
                    EditorGUILayout.HelpBox(
                        "All detected SkinnedMeshRenderers are ignored. Select at least one mesh to bake.",
                        MessageType.Warning);
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

                // Display detected Animator and route its clips through Animation Merge
                // before they become VAT bake candidates.
                EditorGUI.BeginChangeCheck();
                Animator selectedAnimator = (Animator)EditorGUILayout.ObjectField(
                    "Detected Animator",
                    _detectedAnimator,
                    typeof(Animator),
                    true);
                if (EditorGUI.EndChangeCheck())
                {
                    SetDetectedAnimator(selectedAnimator);
                }

                DrawAnimationMergeInput();

                string clipSourceLabel = _usingAnimationMergeOutputs
                    ? "Animator + Animation Merge outputs"
                    : "Detected Animator controller";
                _clipsFoldout = EditorGUILayout.Foldout(
                    _clipsFoldout,
                    $"Select Animation Clips to Bake ({_detectedClips.Count}) • {clipSourceLabel}");
                if (_clipsFoldout)
                {
                    EditorGUI.indentLevel++;
                    if (_detectedClips.Count == 0)
                    {
                        EditorGUILayout.HelpBox(
                            "No AnimationClip candidate was found from the detected Animator or Animation Merge outputs.",
                            MessageType.Info);
                    }

                    for (int i = 0; i < _detectedClips.Count; i++)
                    {
                        EditorGUILayout.BeginHorizontal();
                        bool selected = EditorGUILayout.Toggle(
                            _selectedClipToggles[i],
                            GUILayout.Width(20));
                        if (selected != _selectedClipToggles[i])
                        {
                            SetClipBakeSelection(_detectedClips[i], selected);
                        }
                        EditorGUILayout.ObjectField(
                            $"Clip [{i}]",
                            _detectedClips[i],
                            typeof(AnimationClip),
                            false);
                        EditorGUILayout.LabelField(
                            IsAnimationMergeOutput(_detectedClips[i]) ? "Merged" : "Animator",
                            GUILayout.Width(58));
                        if (!_selectedClipToggles[i])
                        {
                            EditorGUILayout.LabelField("Ignored", GUILayout.Width(55));
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.indentLevel--;
                }

                if (_detectedClips.Count > 0 && GetSelectedClipCount() == 0)
                {
                    EditorGUILayout.HelpBox(
                        "All detected animation clips are ignored. Select at least one clip to bake.",
                        MessageType.Warning);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Drag a target character GameObject here to load meshes, materials and animation clips.",
                    MessageType.Warning);
            }
            EditorGUILayout.EndVertical();
        }
        private void DrawAnimationMergeInput()
        {
            bool hasMergeInput = _detectedAnimator != null &&
                                 _detectedAnimator.runtimeAnimatorController != null;
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Animation Merge Graph", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This section hosts the original Animation Merge graph, including node connections, Merge inspector, Preview and session actions. The graph is created only after loading.",
                MessageType.Info);

            EditorGUI.BeginDisabledGroup(!hasMergeInput);
            if (GUILayout.Button(
                    _embeddedAnimationMergeGraphLoaded
                        ? "Reload Original Animation Merge Graph"
                        : "Load Original Animation Merge Graph"))
            {
                LoadEmbeddedAnimationMergeGraph();
            }
            EditorGUI.EndDisabledGroup();

            if (_detectedAnimator == null)
            {
                EditorGUILayout.HelpBox(
                    "A detected Animator with a runtime controller is required before Animation Merge can preprocess clips.",
                    MessageType.Warning);
            }
            else if (!hasMergeInput)
            {
                EditorGUILayout.HelpBox(
                    "The detected Animator has no runtime Animator Controller to export.",
                    MessageType.Warning);
            }
            else if (_usingAnimationMergeOutputs)
            {
                EditorGUILayout.HelpBox(
                    $"{_animationMergeOutputs.Count} generated output(s) are available alongside {_controllerClips.Count} original controller clip(s). Use the Bake toggle on graph nodes or Select Animation Clips to Bake below.",
                    MessageType.Info);
            }

            if (_embeddedAnimationMergeGraphLoaded)
            {
                EditorGUILayout.HelpBox(
                    "The original graph is displayed in the embedded graph surface above. Its Animation node Bake toggles drive the bake clip selection. Use Exit Graph on the graph toolbar to return to VAT Bake Inputs; the temporary graph session remains available while this tool is open.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Animation Merge Graph is not loaded. Press Load Original Animation Merge Graph to open it. If you exited an existing graph, its temporary session will be restored while this tool remains open.",
                    MessageType.None);
            }

            EditorGUILayout.EndVertical();
        }

        private void LoadEmbeddedAnimationMergeGraph()
        {
            if (_detectedAnimator == null || _detectedAnimator.runtimeAnimatorController == null)
            {
                return;
            }

            // Exiting the graph only hides its view. Reattach the same in-memory
            // session so merge nodes, connections and Bake toggles survive while
            // the VAT Bake Tool remains open.
            if (_embeddedAnimationMergeGraph != null &&
                !_embeddedAnimationMergeGraph.IsDisposed)
            {
                _embeddedAnimationMergeSurface = _embeddedAnimationMergeGraph.Root;
                _embeddedAnimationMergeGraph.Show(rootVisualElement);
                _embeddedAnimationMergeGraphLoaded = true;
                Repaint();
                return;
            }

            DisposeEmbeddedAnimationMergeGraph();
            Animator sourceAnimator = _detectedAnimator;
            _embeddedAnimationMergeGraph = AnimationMergeGraphWindow.CreateEmbeddedGraph(
                sourceAnimator,
                selection => HandleAnimationMergeSelection(sourceAnimator, selection),
                ExitEmbeddedAnimationMergeGraph);
            _embeddedAnimationMergeSurface = _embeddedAnimationMergeGraph.Root;
            _embeddedAnimationMergeSurface.name = "AnimationMergeGraphSurface";
            _embeddedAnimationMergeSurface.style.position = Position.Absolute;
            _embeddedAnimationMergeSurface.style.backgroundColor = new Color(0.08f, 0.09f, 0.11f, 1f);
            _embeddedAnimationMergeGraph.Show(rootVisualElement);
            _embeddedAnimationMergeGraphLoaded = true;
            _embeddedAnimationMergeGraph.LoadAnimator(sourceAnimator);
            Repaint();
        }

        private void ExitEmbeddedAnimationMergeGraph()
        {
            if (_embeddedAnimationMergeGraph == null ||
                _embeddedAnimationMergeGraph.IsDisposed)
            {
                return;
            }

            _embeddedAnimationMergeSurface = null;
            _embeddedAnimationMergeGraphLoaded = false;
            Repaint();
        }

        private void SetClipBakeSelection(AnimationClip clip, bool selected)
        {
            for (int i = 0; i < _detectedClips.Count; i++)
            {
                if (_detectedClips[i] == clip && i < _selectedClipToggles.Count)
                {
                    _selectedClipToggles[i] = selected;
                }
            }

            if (_embeddedAnimationMergeGraph != null)
            {
                _embeddedAnimationMergeGraph.SetClipBakeSelection(clip, selected);
            }
        }

        private void HandleAnimationMergeSelection(
            Animator sourceAnimator,
            AnimationMergeBakeSelection selection)
        {
            // Ignore output from a merge session that belongs to a previous target.
            if (_detectedAnimator != sourceAnimator || selection == null ||
                (selection.SourceAnimator != null && selection.SourceAnimator != _detectedAnimator))
            {
                return;
            }

            Dictionary<AnimationClip, bool> previousSelections =
                new Dictionary<AnimationClip, bool>();
            for (int i = 0; i < _detectedClips.Count; i++)
            {
                AnimationClip clip = _detectedClips[i];
                if (clip != null && !previousSelections.ContainsKey(clip))
                {
                    previousSelections.Add(
                        clip,
                        i < _selectedClipToggles.Count && _selectedClipToggles[i]);
                }
            }

            List<AnimationClip> candidates = new List<AnimationClip>();
            for (int i = 0; i < selection.Candidates.Count; i++)
            {
                AnimationClip clip = selection.Candidates[i];
                if (clip != null && !candidates.Contains(clip))
                {
                    candidates.Add(clip);
                }
            }

            for (int i = 0; i < _controllerClips.Count; i++)
            {
                AnimationClip clip = _controllerClips[i];
                if (clip != null && !candidates.Contains(clip))
                {
                    candidates.Add(clip);
                }
            }

            _animationMergeOutputs.Clear();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!_controllerClips.Contains(candidates[i]))
                {
                    _animationMergeOutputs.Add(candidates[i]);
                }
            }

            HashSet<AnimationClip> selectedClips = new HashSet<AnimationClip>(selection.Selected);
            _detectedClips = candidates;
            _selectedClipToggles = new List<bool>();
            for (int i = 0; i < _detectedClips.Count; i++)
            {
                AnimationClip clip = _detectedClips[i];
                bool selected = selection.Candidates.Contains(clip)
                    ? selectedClips.Contains(clip)
                    : previousSelections.TryGetValue(clip, out bool previous) && previous;
                _selectedClipToggles.Add(selected);
            }

            _usingAnimationMergeOutputs = _animationMergeOutputs.Count > 0;
            _clipsFoldout = true;
            Repaint();
        }

        private void DrawOutputsPage()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("VAT Asset Data SO", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Source of truth for this output. All preview fields below are read-only and are loaded from this asset.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            VATAssetDataSO selectedAsset = (VATAssetDataSO)EditorGUILayout.ObjectField(
                "VAT Asset Data SO",
                _outputAssetData,
                typeof(VATAssetDataSO),
                false);
            if (EditorGUI.EndChangeCheck())
            {
                _outputAssetData = selectedAsset;
            }

            if (_outputAssetData == null)
            {
                EditorGUILayout.HelpBox("Bake an output or select an existing VATAssetDataSO to inspect its generated data.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            string assetDataPath = AssetDatabase.GetAssetPath(_outputAssetData);
            EditorGUILayout.LabelField("Asset Path", string.IsNullOrEmpty(assetDataPath) ? "Not saved as an asset" : assetDataPath);
            EditorGUILayout.HelpBox(
                $"Bake will update the existing output '{_outputAssetData.name}' after confirmation.",
                MessageType.Warning);
            _outputPreviewFoldout = EditorGUILayout.Foldout(
                _outputPreviewFoldout,
                "Derived Output Preview (Read Only)",
                true);
            if (_outputPreviewFoldout)
            {
                DrawReadOnlyAssetDataPreview(_outputAssetData);
            }

            _vatTextureQualityFoldout = EditorGUILayout.Foldout(
                _vatTextureQualityFoldout,
                "VAT Texture Quality & Luna Protection",
                true);
            if (_vatTextureQualityFoldout)
            {
                DrawVATTextureQualityPreview(_outputAssetData.VATTexture);
            }

            _materialTextureQualityFoldout = EditorGUILayout.Foldout(
                _materialTextureQualityFoldout,
                "Material Base Texture Quality & Luna Protection",
                true);
            if (_materialTextureQualityFoldout)
            {
                DrawMaterialBaseTextureQualityPreview(_outputAssetData);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawVATSetupPage()
        {
            InitializeSetupSerializedProperties();

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("VAT Runtime Setup Helper", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Batch-configure baked VAT characters directly from this tool. " +
                "Each target receives VAT_RenderComponent, MeshFilter and MeshRenderer; " +
                "legacy 'MeshRenderer_VAT' children are cleaned up.",
                MessageType.Info);

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Shared Settings", EditorStyles.boldLabel);
            _setupVATAssetData = (VATAssetDataSO)EditorGUILayout.ObjectField(
                "VAT Asset Data SO",
                _setupVATAssetData,
                typeof(VATAssetDataSO),
                false);
            _setupVATMaterial = (Material)EditorGUILayout.ObjectField(
                "VAT Material (Optional)",
                _setupVATMaterial,
                typeof(Material),
                false);

            if (_setupVATAssetData == null && _outputAssetData != null &&
                GUILayout.Button("Use VAT Asset Data from Outputs"))
            {
                _setupVATAssetData = _outputAssetData;
            }
            EditorGUILayout.EndVertical();

            _setupSerializedObject.Update();
            EditorGUILayout.PropertyField(_setupTargetRootsProperty, new GUIContent("Target GameObjects"), true);

            if (GUILayout.Button("+ Add Selected Objects from Hierarchy"))
            {
                AddSelectedSetupObjects();
                _setupSerializedObject.Update();
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(
                _setupAttachmentsProperty,
                new GUIContent("Equipment Attachments (Applied to All)"),
                true);
            _setupSerializedObject.ApplyModifiedProperties();
            EditorGUILayout.EndVertical();
        }

        private void DrawVATSetupFooter()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Runtime Setup Action & Validation", EditorStyles.boldLabel);

            int validTargetCount = GetValidSetupTargetCount();
            bool hasVATMaterial = _setupVATMaterial != null ||
                                  (_setupVATAssetData != null &&
                                   _setupVATAssetData.BakedMaterials != null &&
                                   _setupVATAssetData.BakedMaterials.Count > 0);
            bool canSetup = validTargetCount > 0 && _setupVATAssetData != null && hasVATMaterial;

            if (_setupVATAssetData == null)
            {
                EditorGUILayout.HelpBox("Assign a VATAssetDataSO in the Runtime Setup tab.", MessageType.Warning);
            }
            else if (validTargetCount == 0)
            {
                EditorGUILayout.HelpBox("Add at least one target GameObject for runtime setup.", MessageType.Warning);
            }
            else if (!hasVATMaterial)
            {
                EditorGUILayout.HelpBox(
                    "Assign a VAT Material or use a VATAssetDataSO containing baked materials.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"Ready to set up {validTargetCount} VAT character(s). A VATSystem is created automatically if missing.",
                    MessageType.Info);
            }

            EditorGUI.BeginDisabledGroup(!canSetup);
            if (GUILayout.Button($"Setup {validTargetCount} VAT Character(s)", GUILayout.Height(30)))
            {
                VATSetupHelper.SetupAllVATCharacters(
                    _setupTargetRoots,
                    _setupVATAssetData,
                    _setupVATMaterial,
                    _setupAttachments);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndVertical();
        }

        private void InitializeSetupSerializedProperties()
        {
            if (_setupSerializedObject != null &&
                _setupSerializedObject.targetObject == this &&
                _setupTargetRootsProperty != null &&
                _setupAttachmentsProperty != null)
            {
                return;
            }

            if (_setupTargetRoots == null) _setupTargetRoots = new List<GameObject>();
            if (_setupAttachments == null) _setupAttachments = new List<VATSetupHelper.SocketAttachmentSetup>();

            _setupSerializedObject = new SerializedObject(this);
            _setupTargetRootsProperty = _setupSerializedObject.FindProperty("_setupTargetRoots");
            _setupAttachmentsProperty = _setupSerializedObject.FindProperty("_setupAttachments");
        }

        private void AddSelectedSetupObjects()
        {
            GameObject[] selectedObjects = Selection.gameObjects;
            if (selectedObjects == null || selectedObjects.Length == 0)
            {
                Debug.LogWarning("[VATBakeTool] No GameObjects selected in the Hierarchy.");
                return;
            }

            int addedCount = 0;
            for (int i = 0; i < selectedObjects.Length; i++)
            {
                GameObject selectedObject = selectedObjects[i];
                if (selectedObject != null && !_setupTargetRoots.Contains(selectedObject))
                {
                    _setupTargetRoots.Add(selectedObject);
                    addedCount++;
                }
            }

            Debug.Log($"[VATBakeTool] Added {addedCount} setup target(s) from the Hierarchy selection.");
        }

        private int GetValidSetupTargetCount()
        {
            int validTargetCount = 0;
            for (int i = 0; i < _setupTargetRoots.Count; i++)
            {
                if (_setupTargetRoots[i] != null) validTargetCount++;
            }

            return validTargetCount;
        }

        private void DrawCommonFooter()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Bake Action & Validation", EditorStyles.boldLabel);

            int activeMeshCount = GetSelectedMeshCount();
            int selectedClipCount = GetSelectedClipCount();
            bool isReadyToBake = _targetPrefab != null && activeMeshCount > 0 && selectedClipCount > 0;

            if (!isReadyToBake)
            {
                string blockingError = _targetPrefab == null
                    ? "[Error] Bake is blocked: select a target GameObject in the Inputs tab."
                    : activeMeshCount == 0
                        ? "[Error] Bake is blocked: select at least one SkinnedMeshRenderer in the Inputs tab."
                        : "[Error] Bake is blocked: select at least one animation clip in the Inputs tab.";
                EditorGUILayout.HelpBox(blockingError, MessageType.Error);
            }

            string buttonLabel = _outputAssetData != null ? "Bake VAT Assets..." : "Simulate VAT Baking Pipeline";
            EditorGUI.BeginDisabledGroup(!isReadyToBake);
            if (GUILayout.Button(buttonLabel, GUILayout.Height(30)))
            {
                BakeVATSimulation();
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndVertical();
        }

        private void SyncMeshSelectionToggles()
        {
            while (_selectedMeshToggles.Count < _detectedSkinnedMeshes.Count)
                _selectedMeshToggles.Add(true);
            while (_selectedMeshToggles.Count > _detectedSkinnedMeshes.Count)
                _selectedMeshToggles.RemoveAt(_selectedMeshToggles.Count - 1);

            while (_meshRoles.Count < _detectedSkinnedMeshes.Count)
                _meshRoles.Add(VATRendererRole.Body);
            while (_meshRoles.Count > _detectedSkinnedMeshes.Count)
                _meshRoles.RemoveAt(_meshRoles.Count - 1);

            while (_weaponOutputNames.Count < _detectedSkinnedMeshes.Count)
                _weaponOutputNames.Add("Weapon");
            while (_weaponOutputNames.Count > _detectedSkinnedMeshes.Count)
                _weaponOutputNames.RemoveAt(_weaponOutputNames.Count - 1);
        }

        private int GetSelectedMeshCount()
        {
            int selectedCount = 0;
            for (int i = 0; i < _selectedMeshToggles.Count; i++)
            {
                if (_selectedMeshToggles[i]) selectedCount++;
            }

            return selectedCount;
        }

        private int GetSelectedClipCount()
        {
            int selectedCount = 0;
            for (int i = 0; i < _selectedClipToggles.Count; i++)
            {
                if (_selectedClipToggles[i]) selectedCount++;
            }

            return selectedCount;
        }

        private static void DrawReadOnlyAssetDataPreview(VATAssetDataSO assetData)
        {
            EditorGUILayout.Space();

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.ObjectField("Baked Static Mesh", assetData.BakedStaticMesh, typeof(Mesh), false);
            EditorGUILayout.IntField("Total Vertices", assetData.TotalVertices);
            EditorGUILayout.IntField("Total Frames", assetData.TotalFrames);
            EditorGUILayout.Vector3Field("Bounding Min", assetData.BoundingMin);
            EditorGUILayout.Vector3Field("Bounding Max", assetData.BoundingMax);

            int materialCount = assetData.BakedMaterials != null ? assetData.BakedMaterials.Count : 0;
            EditorGUILayout.LabelField("Baked Materials", materialCount.ToString());
            for (int i = 0; i < materialCount; i++)
            {
                EditorGUILayout.ObjectField($"Material [{i}]", assetData.BakedMaterials[i], typeof(Material), false);
            }

            int clipCount = assetData.Clips != null ? assetData.Clips.Count : 0;
            EditorGUILayout.LabelField("Baked Clips", clipCount.ToString());
            for (int i = 0; i < clipCount; i++)
            {
                VATClipInfo clip = assetData.Clips[i];
                if (clip == null) continue;
                EditorGUILayout.LabelField(
                    $"Clip [{i}]",
                    $"{clip.ClipName} | Frames {clip.StartFrame}-{clip.EndFrame} | {clip.FrameRate:0.##} FPS");
            }

            int weaponAssetCount = assetData.WeaponAssets != null ? assetData.WeaponAssets.Count : 0;
            EditorGUILayout.LabelField("Weapon VAT Channels", weaponAssetCount.ToString());
            for (int i = 0; i < weaponAssetCount; i++)
            {
                VATWeaponAssetEntry entry = assetData.WeaponAssets[i];
                VATWeaponAssetSO weaponAsset = entry != null ? entry.WeaponAsset : null;
                string weaponName = entry == null || string.IsNullOrWhiteSpace(entry.WeaponName)
                    ? "Weapon"
                    : entry.WeaponName;
                EditorGUILayout.ObjectField($"Weapon [{weaponName}]", weaponAsset, typeof(VATWeaponAssetSO), false);
                if (weaponAsset != null)
                {
                    EditorGUILayout.IntField($"{weaponName} Vertices", weaponAsset.TotalVertices);
                    EditorGUILayout.IntField($"{weaponName} Frames", weaponAsset.TotalFrames);
                }
            }

            if (weaponAssetCount == 0 && assetData.DefaultWeaponAsset != null)
            {
                EditorGUILayout.ObjectField(
                    "Legacy Default Weapon",
                    assetData.DefaultWeaponAsset,
                    typeof(VATWeaponAssetSO),
                    false);
            }

            int socketCount = assetData.Sockets != null ? assetData.Sockets.Count : 0;
            EditorGUILayout.LabelField("Socket Data", socketCount == 0 ? "Disabled" : socketCount.ToString());
            EditorGUI.EndDisabledGroup();
        }

        private void DrawVATTextureQualityPreview(Texture2D vatTexture)
        {
            EditorGUILayout.Space();

            if (vatTexture == null)
            {
                EditorGUILayout.HelpBox("No VAT texture is assigned to this VATAssetDataSO.", MessageType.Error);
                return;
            }

            string textureAssetPath = AssetDatabase.GetAssetPath(vatTexture);
            TextureImporter importer = AssetImporter.GetAtPath(textureAssetPath) as TextureImporter;
            int requiredDimension = Mathf.Max(vatTexture.width, vatTexture.height);

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.ObjectField("Texture Asset", vatTexture, typeof(Texture2D), false);
            EditorGUILayout.LabelField("Dimensions", $"{vatTexture.width} x {vatTexture.height}");
            EditorGUILayout.LabelField("VAT Limit", $"{MaxVATTextureDimension} x {MaxVATTextureDimension}");
            EditorGUI.EndDisabledGroup();

            bool unityImporterProtected = IsVATTextureImporterProtected(importer, requiredDimension);
            string unityStatus = unityImporterProtected
                ? "Unity importer: Linear, Point, Clamp, no mipmaps, RGBA32 and uncompressed for Default/WebGL."
                : "Unity importer protection is incomplete. Re-bake this VAT asset to restore the required importer settings.";
            EditorGUILayout.HelpBox(unityStatus, unityImporterProtected ? MessageType.Info : MessageType.Error);

            LunaTextureOverrideSettings lunaSettings;
            bool hasLunaOverride = TryGetCachedLunaTextureOverride(textureAssetPath, out lunaSettings);
            bool lunaProtected = hasLunaOverride && IsLunaTextureProtected(lunaSettings, requiredDimension);

            if (hasLunaOverride)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.LabelField(
                    "Luna Override",
                    $"{lunaSettings.MaxWidth} x {lunaSettings.MaxHeight} | {lunaSettings.Format} | {lunaSettings.Compression} | Quality {lunaSettings.Quality}");
                EditorGUI.EndDisabledGroup();
            }

            string lunaStatus = lunaProtected
                ? "Luna Playground: protected from resize and texture compression for this VAT texture."
                : "Luna Playground protection is missing or unsafe. This can resize or quantize VAT data during build.";
            EditorGUILayout.HelpBox(lunaStatus, lunaProtected ? MessageType.Info : MessageType.Error);

            if (!lunaProtected && !string.IsNullOrEmpty(textureAssetPath) &&
                GUILayout.Button("Apply Luna VAT Texture Protection"))
            {
                if (RegisterAssetInLunaJson(textureAssetPath))
                {
                    InvalidateLunaTextureOverrideCache();
                    Repaint();
                }
            }
        }

        private void DrawMaterialBaseTextureQualityPreview(VATAssetDataSO assetData)
        {
            EditorGUILayout.Space();

            List<Texture2D> baseTextures = CollectMaterialBaseTextures(assetData != null ? assetData.BakedMaterials : null);
            if (baseTextures.Count == 0)
            {
                EditorGUILayout.HelpBox("The baked materials do not use a Texture2D base texture.", MessageType.Warning);
                return;
            }

            bool allProtected = true;
            for (int i = 0; i < baseTextures.Count; i++)
            {
                Texture2D texture = baseTextures[i];
                string textureAssetPath = AssetDatabase.GetAssetPath(texture);
                int requiredDimension = Mathf.Max(texture.width, texture.height);
                LunaTextureOverrideSettings lunaSettings;
                bool hasLunaOverride = TryGetCachedLunaTextureOverride(textureAssetPath, out lunaSettings);
                bool isProtected = hasLunaOverride && IsLunaTextureProtected(lunaSettings, requiredDimension);
                allProtected &= isProtected;

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField($"Base Texture [{i}]", texture, typeof(Texture2D), false);
                EditorGUILayout.LabelField(
                    "Luna Export",
                    hasLunaOverride
                        ? $"{lunaSettings.MaxWidth} x {lunaSettings.MaxHeight} | {lunaSettings.Format} | {lunaSettings.Compression} | Quality {lunaSettings.Quality}"
                        : "No per-texture override");
                EditorGUI.EndDisabledGroup();

                if (!isProtected)
                {
                    EditorGUILayout.HelpBox(
                        $"'{texture.name}' can be resized or quantized by Luna's default texture rule.",
                        MessageType.Error);
                }
            }

            if (allProtected)
            {
                EditorGUILayout.HelpBox(
                    "All VAT material base textures are protected from Luna resize and lossy PNG quantization.",
                    MessageType.Info);
                return;
            }

            if (GUILayout.Button("Apply Luna Material Texture Protection"))
            {
                bool success = true;
                for (int i = 0; i < baseTextures.Count; i++)
                {
                    Texture2D texture = baseTextures[i];
                    string textureAssetPath = AssetDatabase.GetAssetPath(texture);
                    success &= RegisterTextureInLunaJson(textureAssetPath, Mathf.Max(texture.width, texture.height));
                }

                InvalidateLunaTextureOverrideCache();
                if (!success)
                {
                    Debug.LogError("[VATBakeTool] Some VAT material base textures could not be protected in luna.json.");
                }

                Repaint();
            }
        }

        private static List<Texture2D> CollectMaterialBaseTextures(List<Material> materials)
        {
            List<Texture2D> textures = new List<Texture2D>();
            if (materials == null) return textures;

            for (int i = 0; i < materials.Count; i++)
            {
                Texture2D texture = GetMaterialBaseTexture(materials[i]) as Texture2D;
                if (texture != null && !textures.Contains(texture))
                {
                    textures.Add(texture);
                }
            }

            return textures;
        }

        private static Texture GetMaterialBaseTexture(Material material)
        {
            if (material == null) return null;

            Texture texture = material.HasProperty("_BaseMap")
                ? material.GetTexture("_BaseMap")
                : null;
            return texture != null || !material.HasProperty("_MainTex")
                ? texture
                : material.GetTexture("_MainTex");
        }

        private bool TryGetCachedLunaTextureOverride(string assetPath, out LunaTextureOverrideSettings settings)
        {
            string normalizedPath = string.IsNullOrEmpty(assetPath) ? string.Empty : assetPath.Replace('\\', '/');
            string lunaJsonPath = GetLunaJsonPath();
            DateTime lastWriteTimeUtc = File.Exists(lunaJsonPath)
                ? File.GetLastWriteTimeUtc(lunaJsonPath)
                : DateTime.MinValue;

            if (string.Equals(_cachedLunaTexturePath, normalizedPath, StringComparison.Ordinal) &&
                _cachedLunaJsonWriteTimeUtc == lastWriteTimeUtc)
            {
                settings = _cachedLunaOverrideSettings;
                return _cachedLunaOverrideFound;
            }

            _cachedLunaTexturePath = normalizedPath;
            _cachedLunaJsonWriteTimeUtc = lastWriteTimeUtc;
            _cachedLunaOverrideFound = TryGetLunaTextureOverride(normalizedPath, out _cachedLunaOverrideSettings);
            settings = _cachedLunaOverrideSettings;
            return _cachedLunaOverrideFound;
        }

        private void InvalidateLunaTextureOverrideCache()
        {
            _cachedLunaTexturePath = null;
            _cachedLunaJsonWriteTimeUtc = DateTime.MinValue;
            _cachedLunaOverrideFound = false;
            _cachedLunaOverrideSettings = new LunaTextureOverrideSettings();
        }

        private static bool IsVATTextureImporterProtected(TextureImporter importer, int requiredDimension)
        {
            if (importer == null || requiredDimension > MaxVATTextureDimension)
            {
                return false;
            }

            TextureImporterPlatformSettings defaultSettings = importer.GetDefaultPlatformTextureSettings();
            TextureImporterPlatformSettings webglSettings = importer.GetPlatformTextureSettings("WebGL");
            bool defaultProtected = IsVATTexturePlatformProtected(defaultSettings, requiredDimension);
            bool webglProtected = IsVATTexturePlatformProtected(webglSettings, requiredDimension);

            return !importer.sRGBTexture &&
                   importer.textureCompression == TextureImporterCompression.Uncompressed &&
                   importer.filterMode == FilterMode.Point &&
                   importer.wrapMode == TextureWrapMode.Clamp &&
                   !importer.mipmapEnabled &&
                   !importer.isReadable &&
                   defaultProtected &&
                   webglProtected;
        }

        private static bool IsVATTexturePlatformProtected(TextureImporterPlatformSettings settings, int requiredDimension)
        {
            return settings.overridden &&
                   settings.format == TextureImporterFormat.RGBA32 &&
                   settings.textureCompression == TextureImporterCompression.Uncompressed &&
                   !settings.crunchedCompression &&
                   settings.maxTextureSize >= requiredDimension;
        }

        private void LoadBakeReferences()
        {
            _detectedSkinnedMeshes.Clear();
            _selectedMeshToggles.Clear();
            _meshRoles.Clear();
            _weaponOutputNames.Clear();
            _detectedMaterials.Clear();
            _controllerClips.Clear();
            _animationMergeOutputs.Clear();
            _detectedClips.Clear();
            _selectedClipToggles.Clear();
            _detectedAnimator = null;
            _usingAnimationMergeOutputs = false;
            DisposeEmbeddedAnimationMergeGraph();

            if (_targetPrefab == null) return;

            // Find SkinnedMeshRenderers
            SkinnedMeshRenderer[] smrs = _targetPrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            _detectedSkinnedMeshes.AddRange(smrs);
            // Default all skinned meshes as selected (checked)
            for (int i = 0; i < smrs.Length; i++)
            {
                _selectedMeshToggles.Add(true);
                _meshRoles.Add(GuessRendererRole(smrs[i]));
                _weaponOutputNames.Add("Weapon");
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

        private static VATRendererRole GuessRendererRole(SkinnedMeshRenderer renderer)
        {
            if (renderer == null)
            {
                return VATRendererRole.Body;
            }

            string path = renderer.transform.name.ToLowerInvariant();
            string hierarchyPath = renderer.transform.root == null
                ? path
                : renderer.transform.root.name.ToLowerInvariant() + "/" + path;
            return hierarchyPath.Contains("weapon") || hierarchyPath.Contains("sword") ||
                   hierarchyPath.Contains("bow") || hierarchyPath.Contains("shield")
                   ? VATRendererRole.Weapon
                   : VATRendererRole.Body;
        }

        private string GetConfiguredOutputName()
        {
            string configuredName = string.IsNullOrWhiteSpace(_outputName)
                ? _targetPrefab.name
                : _outputName.Trim();
            return GetAssetNameToken(configuredName) + "_VAT";
        }

        private string GetWeaponOutputName(int rendererIndex)
        {
            string weaponName = rendererIndex >= 0 && rendererIndex < _weaponOutputNames.Count
                ? _weaponOutputNames[rendererIndex]
                : null;
            return string.IsNullOrWhiteSpace(weaponName) ? "Weapon" : weaponName.Trim();
        }

        private static string GetAssetNameToken(string value)
        {
            string source = string.IsNullOrWhiteSpace(value) ? "Unnamed" : value.Trim();
            char[] invalidChars = Path.GetInvalidFileNameChars();
            char[] characters = source.ToCharArray();
            for (int i = 0; i < characters.Length; i++)
            {
                if (Array.IndexOf(invalidChars, characters[i]) >= 0 ||
                    characters[i] == '/' || characters[i] == '\\')
                {
                    characters[i] = '_';
                }
            }

            return new string(characters);
        }

        private void ConfigureActiveOutputDirectory(string outputName, VATAssetDataSO originalAssetData)
        {
            string parentDirectory = _savePath.Replace('\\', '/').TrimEnd('/');
            string assetDataName = outputName + "Data";
            if (originalAssetData != null)
            {
                string originalAssetPath = AssetDatabase.GetAssetPath(originalAssetData);
                if (!string.IsNullOrEmpty(originalAssetPath))
                {
                    string originalParentDirectory = Path.GetDirectoryName(originalAssetPath);
                    if (!string.IsNullOrEmpty(originalParentDirectory))
                    {
                        parentDirectory = originalParentDirectory.Replace('\\', '/');
                    }
                    assetDataName = originalAssetData.name;
                }
            }

            _activeOutputDirectory = Path.Combine(parentDirectory, GetAssetNameToken(assetDataName))
                .Replace('\\', '/');
            if (!Directory.Exists(_activeOutputDirectory))
            {
                Directory.CreateDirectory(_activeOutputDirectory);
                AssetDatabase.Refresh();
            }
        }

        private string GetActiveOutputDirectory()
        {
            return string.IsNullOrEmpty(_activeOutputDirectory) ? _savePath : _activeOutputDirectory;
        }

        private void SetDetectedAnimator(Animator animator)
        {
            _detectedAnimator = animator;
            DisposeEmbeddedAnimationMergeGraph();
            DetectAnimationClips();
        }

        private void DetectAnimationClips()
        {
            _controllerClips.Clear();
            _animationMergeOutputs.Clear();
            _detectedClips.Clear();
            _selectedClipToggles.Clear();
            _usingAnimationMergeOutputs = false;

            if (_detectedAnimator == null || _detectedAnimator.runtimeAnimatorController == null)
            {
                return;
            }

            AnimationClip[] controllerClips = _detectedAnimator.runtimeAnimatorController.animationClips;
            for (int i = 0; i < controllerClips.Length; i++)
            {
                AnimationClip clip = controllerClips[i];
                if (clip != null && !_controllerClips.Contains(clip))
                {
                    _controllerClips.Add(clip);
                }
            }

            RebuildDetectedClipCandidates();
        }

        private void RebuildDetectedClipCandidates()
        {
            Dictionary<AnimationClip, bool> previousSelections =
                new Dictionary<AnimationClip, bool>();
            for (int i = 0; i < _detectedClips.Count; i++)
            {
                AnimationClip clip = _detectedClips[i];
                if (clip != null && !previousSelections.ContainsKey(clip))
                {
                    bool isSelected = i < _selectedClipToggles.Count && _selectedClipToggles[i];
                    previousSelections.Add(clip, isSelected);
                }
            }

            _detectedClips.Clear();
            _selectedClipToggles.Clear();
            AddClipCandidates(_controllerClips, previousSelections);
            AddClipCandidates(_animationMergeOutputs, previousSelections);
            _usingAnimationMergeOutputs = _animationMergeOutputs.Count > 0;
        }

        private void AddClipCandidates(
            IList<AnimationClip> clips,
            Dictionary<AnimationClip, bool> previousSelections)
        {
            if (clips == null)
            {
                return;
            }

            for (int i = 0; i < clips.Count; i++)
            {
                AnimationClip clip = clips[i];
                if (clip == null || _detectedClips.Contains(clip))
                {
                    continue;
                }

                _detectedClips.Add(clip);
                bool isSelected;
                _selectedClipToggles.Add(
                    previousSelections.TryGetValue(clip, out isSelected) ? isSelected : true);
            }
        }

        private bool IsAnimationMergeOutput(AnimationClip clip)
        {
            return clip != null && _animationMergeOutputs.Contains(clip);
        }

        private void BakeVATSimulation()
        {
            if (_targetPrefab == null || _detectedSkinnedMeshes.Count == 0) return;

            // Build one Body channel and one named VAT channel for every Weapon group.
            List<SkinnedMeshRenderer> bodyRenderers = new List<SkinnedMeshRenderer>();
            Dictionary<string, WeaponBakeGroup> weaponGroupsByName =
                new Dictionary<string, WeaponBakeGroup>(StringComparer.OrdinalIgnoreCase);
            List<WeaponBakeGroup> weaponGroups = new List<WeaponBakeGroup>();
            for (int i = 0; i < _detectedSkinnedMeshes.Count; i++)
            {
                if (i < _selectedMeshToggles.Count && _selectedMeshToggles[i] && _detectedSkinnedMeshes[i] != null)
                {
                    VATRendererRole role = i < _meshRoles.Count ? _meshRoles[i] : VATRendererRole.Body;
                    if (role == VATRendererRole.Weapon)
                    {
                        string weaponName = GetWeaponOutputName(i);
                        WeaponBakeGroup weaponGroup;
                        if (!weaponGroupsByName.TryGetValue(weaponName, out weaponGroup))
                        {
                            weaponGroup = new WeaponBakeGroup { Name = weaponName };
                            weaponGroupsByName.Add(weaponName, weaponGroup);
                            weaponGroups.Add(weaponGroup);
                        }

                        weaponGroup.Renderers.Add(_detectedSkinnedMeshes[i]);
                    }
                    else
                    {
                        bodyRenderers.Add(_detectedSkinnedMeshes[i]);
                    }
                }
            }

            if (bodyRenderers.Count == 0)
            {
                Debug.LogError("[VATBakeTool] No Body renderer selected for baking. Assign at least one selected renderer to the Body role.");
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

            string outputName = GetConfiguredOutputName();
            ConfigureActiveOutputDirectory(outputName, _outputAssetData);

            // The output mode controls material validation independently for each
            // VAT channel. Body and every named Weapon group have separate
            // meshes/textures/materials but share the exact same clip manifest.
            bool requireUnifiedShaderAndBaseTexture = _outputMode == VATBakeOutputMode.Combined;
            List<Material> bodyMaterialSlots;
            string validationMessage;
            if (!CollectMaterialSlots(
                    bodyRenderers,
                    requireUnifiedShaderAndBaseTexture,
                    out bodyMaterialSlots,
                    out validationMessage))
            {
                Debug.LogError($"[VATBakeTool] {validationMessage}");
                EditorUtility.DisplayDialog("VAT Bake Stopped: Material Validation", validationMessage, "OK");
                return;
            }

            for (int i = 0; i < weaponGroups.Count; i++)
            {
                WeaponBakeGroup weaponGroup = weaponGroups[i];
                if (!CollectMaterialSlots(
                        weaponGroup.Renderers,
                        requireUnifiedShaderAndBaseTexture,
                        out weaponGroup.Materials,
                        out validationMessage))
                {
                    Debug.LogError($"[VATBakeTool] {validationMessage}");
                    EditorUtility.DisplayDialog(
                        $"VAT Bake Stopped: Weapon Material Validation ({weaponGroup.Name})",
                        validationMessage,
                        "OK");
                    return;
                }
            }

            BakedOutput output = BakeOutput(
                bodyRenderers,
                bodyMaterialSlots,
                weaponGroups,
                clipsToBake,
                outputName,
                _outputAssetData,
                overrideMode);

            if (output != null)
            {
                _outputAssetData = output.AssetData;
            }
        }

        private BakedOutput BakeOutput(
            List<SkinnedMeshRenderer> bodyRenderers,
            List<Material> bodyMaterials,
            List<WeaponBakeGroup> weaponGroups,
            List<AnimationClip> clipsToBake,
            string outputName,
            VATAssetDataSO existingAsset,
            bool overrideMode)
        {
            if (bodyRenderers == null || bodyRenderers.Count == 0 ||
                bodyMaterials == null || bodyMaterials.Count == 0)
            {
                return null;
            }

            Shader vatShader = Shader.Find("OptimizedFeature/VAT_Unlit_Luna");
            if (vatShader == null)
            {
                Debug.LogError("[VATBakeTool] Could not find shader 'OptimizedFeature/VAT_Unlit_Luna'.");
                return null;
            }

            List<Material> allMaterials = new List<Material>(bodyMaterials);
            if (weaponGroups != null)
            {
                for (int i = 0; i < weaponGroups.Count; i++)
                {
                    allMaterials.AddRange(weaponGroups[i].Materials);
                }
            }
            if (!EnsureMaterialBaseTexturesLunaProtected(allMaterials))
            {
                return null;
            }

            HashSet<Material> patchedMaterials = new HashSet<Material>();
            for (int i = 0; i < allMaterials.Count; i++)
            {
                Material material = allMaterials[i];
                if (material != null && patchedMaterials.Add(material))
                {
                    ValidateAndPatchMaterialShader(material);
                }
            }

            List<int> clipFramesList = new List<int>();
            int sampleRate = Mathf.Max(1, _sampleFrameRate);
            int totalBakeFrames = 0;
            for (int i = 0; i < clipsToBake.Count; i++)
            {
                int frames = Mathf.Max(1, Mathf.RoundToInt(clipsToBake[i].length * sampleRate));
                clipFramesList.Add(frames);
                totalBakeFrames += frames;
            }

            if (totalBakeFrames > MaxVATTextureDimension)
            {
                Debug.LogError(
                    $"[VATBakeTool] Cannot bake '{outputName}': the shared frame manifest has " +
                    $"{totalBakeFrames} rows, exceeding the supported {MaxVATTextureDimension} rows. " +
                    "Reduce clips or lower the sample rate.");
                return null;
            }

            GameObject animationSampleRoot = _detectedAnimator != null
                ? _detectedAnimator.gameObject
                : _targetPrefab;
            BakedChannelOutput body = BakeVATChannel(
                bodyRenderers,
                bodyMaterials,
                clipsToBake,
                clipFramesList,
                totalBakeFrames,
                outputName + "_Body",
                existingAsset != null ? existingAsset.BakedStaticMesh : null,
                existingAsset != null ? existingAsset.VATTexture : null,
                animationSampleRoot,
                overrideMode);
            if (body == null)
            {
                return null;
            }

            if (weaponGroups != null)
            {
                for (int i = 0; i < weaponGroups.Count; i++)
                {
                    WeaponBakeGroup weaponGroup = weaponGroups[i];
                    string weaponChannelName = outputName + "_Weapon_" + GetAssetNameToken(weaponGroup.Name);
                    weaponGroup.ExistingAsset = existingAsset != null
                        ? existingAsset.GetWeaponAsset(weaponGroup.Name)
                        : null;
                    weaponGroup.Channel = BakeVATChannel(
                        weaponGroup.Renderers,
                        weaponGroup.Materials,
                        clipsToBake,
                        clipFramesList,
                        totalBakeFrames,
                        weaponChannelName,
                        weaponGroup.ExistingAsset != null ? weaponGroup.ExistingAsset.BakedStaticMesh : null,
                        weaponGroup.ExistingAsset != null ? weaponGroup.ExistingAsset.VATTexture : null,
                        animationSampleRoot,
                        overrideMode);
                    if (weaponGroup.Channel == null)
                    {
                        return null;
                    }
                }
            }

            body.Materials = SaveBakedMaterials(
                bodyMaterials,
                body.Texture,
                body.BoundsMin,
                body.BoundsMax,
                totalBakeFrames,
                body.VertexCount,
                outputName + "_Body",
                existingAsset != null ? existingAsset.BakedMaterials : null,
                overrideMode,
                vatShader);
            if (body.Materials.Count != bodyMaterials.Count)
            {
                Debug.LogError($"[VATBakeTool] Failed to create all Body VAT materials for '{outputName}'.");
                return null;
            }

            List<VATWeaponAssetEntry> weaponEntries = new List<VATWeaponAssetEntry>();
            if (weaponGroups != null)
            {
                for (int i = 0; i < weaponGroups.Count; i++)
                {
                    WeaponBakeGroup weaponGroup = weaponGroups[i];
                    string weaponChannelName = outputName + "_Weapon_" + GetAssetNameToken(weaponGroup.Name);
                    BakedChannelOutput weapon = weaponGroup.Channel;
                    weapon.Materials = SaveBakedMaterials(
                        weaponGroup.Materials,
                        weapon.Texture,
                        weapon.BoundsMin,
                        weapon.BoundsMax,
                        totalBakeFrames,
                        weapon.VertexCount,
                        weaponChannelName,
                        weaponGroup.ExistingAsset != null ? weaponGroup.ExistingAsset.BakedMaterials : null,
                        overrideMode,
                        vatShader);
                    if (weapon.Materials.Count != weaponGroup.Materials.Count)
                    {
                        Debug.LogError(
                            $"[VATBakeTool] Failed to create all Weapon VAT materials for '{weaponGroup.Name}'.");
                        return null;
                    }

                    weaponGroup.Asset = SaveVATWeaponAssetData(
                        weapon.Mesh,
                        weapon.Texture,
                        weapon.Materials,
                        weapon.BoundsMin,
                        weapon.BoundsMax,
                        totalBakeFrames,
                        weapon.VertexCount,
                        clipsToBake,
                        clipFramesList,
                        weaponChannelName,
                        weaponGroup.ExistingAsset,
                        overrideMode);
                    weaponEntries.Add(new VATWeaponAssetEntry
                    {
                        WeaponName = weaponGroup.Name,
                        WeaponAsset = weaponGroup.Asset
                    });
                }
            }

            VATAssetDataSO assetData = SaveVATAssetData(
                body.Mesh,
                body.Texture,
                body.Materials,
                body.BoundsMin,
                body.BoundsMax,
                totalBakeFrames,
                body.VertexCount,
                clipsToBake,
                clipFramesList,
                outputName,
                existingAsset,
                overrideMode,
                weaponEntries);

            AssetDatabase.SaveAssets();
            BakedOutput output = new BakedOutput
            {
                Body = body,
                AssetData = assetData
            };
            for (int i = 0; i < weaponEntries.Count; i++)
            {
                output.WeaponAssets.Add(weaponEntries[i].WeaponAsset);
            }

            return output;
        }

        private BakedChannelOutput BakeVATChannel(
            List<SkinnedMeshRenderer> renderers,
            List<Material> materialSlots,
            List<AnimationClip> clipsToBake,
            List<int> clipFramesList,
            int totalBakeFrames,
            string channelName,
            Mesh existingMesh,
            Texture2D existingTexture,
            GameObject animationSampleRoot,
            bool overrideMode)
        {
            List<MeshBakeSource> sources = BuildMeshBakeSources(renderers, materialSlots);
            if (sources.Count == 0)
            {
                Debug.LogError($"[VATBakeTool] No valid mesh source found for '{channelName}'.");
                return null;
            }

            Mesh bakedMesh = BuildCombinedStaticMesh(sources);
            bakedMesh.name = channelName + "_Static";
            int vertexCount = bakedMesh.vertexCount;
            if (vertexCount == 0)
            {
                DestroyImmediate(bakedMesh);
                Debug.LogError($"[VATBakeTool] Mesh '{channelName}' has no vertices.");
                return null;
            }

            if (vertexCount > MaxVATTextureDimension)
            {
                DestroyImmediate(bakedMesh);
                Debug.LogError(
                    $"[VATBakeTool] Cannot bake '{channelName}': VAT texture would be " +
                    $"{vertexCount} x {totalBakeFrames}, exceeding the supported " +
                    $"{MaxVATTextureDimension} x {MaxVATTextureDimension} import limit. " +
                    "Reduce mesh vertices or lower the sample rate.");
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

            Mesh outputMesh = SaveMeshAsset(bakedMesh, channelName, existingMesh, overrideMode);
            Vector3 boundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 boundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            Vector3[] frameVertices = new Vector3[vertexCount];
            Vector3[] firstFrameVertices = new Vector3[vertexCount];
            bool hasFirstFrame = false;
            float maxFrameMotion = 0f;
            bool animatorWasEnabled = _detectedAnimator != null && _detectedAnimator.enabled;
            Texture2D vatTexture = null;
            bool startedAnimationMode = false;
            bool samplingStarted = false;
            if (_detectedAnimator != null)
            {
                _detectedAnimator.enabled = false;
            }

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

                        for (int vertex = 0; vertex < vertexCount; vertex++)
                        {
                            boundsMin = Vector3.Min(boundsMin, frameVertices[vertex]);
                            boundsMax = Vector3.Max(boundsMax, frameVertices[vertex]);
                        }
                    }
                }

                Vector3 padding = (boundsMax - boundsMin) * 0.03f;
                boundsMin -= padding;
                boundsMax += padding;
                Vector3 boundsSize = boundsMax - boundsMin;
                if (boundsSize.x <= 0f) boundsSize.x = 1f;
                if (boundsSize.y <= 0f) boundsSize.y = 1f;
                if (boundsSize.z <= 0f) boundsSize.z = 1f;

                vatTexture = new Texture2D(vertexCount, totalBakeFrames, TextureFormat.RGBAHalf, false);
                vatTexture.name = channelName + "_Texture";
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
                    if (samplingStarted) AnimationMode.EndSampling();
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
                    $"[VATBakeTool] No vertex motion was detected while sampling '{channelName}'. " +
                    $"Animation root: '{animationSampleRoot.name}'. Check that the selected clips bind to this hierarchy.");
            }

            vatTexture.Apply();
            Texture2D outputTexture = SaveVATTextureAsset(
                vatTexture,
                channelName,
                existingTexture,
                vertexCount,
                totalBakeFrames,
                overrideMode);
            DestroyImmediate(vatTexture);
            if (outputTexture == null)
            {
                return null;
            }

            return new BakedChannelOutput
            {
                Mesh = outputMesh,
                Texture = outputTexture,
                BoundsMin = boundsMin,
                BoundsMax = boundsMax,
                VertexCount = vertexCount,
                TotalFrames = totalBakeFrames
            };
        }

        private Texture2D SaveVATTextureAsset(
            Texture2D vatTexture,
            string channelName,
            Texture2D existingTexture,
            int vertexCount,
            int totalBakeFrames,
            bool overrideMode)
        {
            string textureAssetPath = overrideMode && existingTexture != null
                ? AssetDatabase.GetAssetPath(existingTexture)
                : AssetDatabase.GenerateUniqueAssetPath(
                    Path.Combine(GetActiveOutputDirectory(), vatTexture.name + ".png").Replace('\\', '/'));

            if (string.IsNullOrEmpty(textureAssetPath))
            {
                Debug.LogError($"[VATBakeTool] Could not resolve texture path for '{channelName}'.");
                return null;
            }

            File.WriteAllBytes(textureAssetPath, vatTexture.EncodeToPNG());
            AssetDatabase.ImportAsset(textureAssetPath, ImportAssetOptions.ForceUpdate);
            ConfigureVATTextureImporter(textureAssetPath);
            TextureImporter vatImporter = AssetImporter.GetAtPath(textureAssetPath) as TextureImporter;
            int requiredDimension = Mathf.Max(vertexCount, totalBakeFrames);
            if (!IsVATTextureImporterProtected(vatImporter, requiredDimension))
            {
                Debug.LogError(
                    $"[VATBakeTool] Unity importer protection could not be verified for '{textureAssetPath}'. " +
                    "Bake stopped so this VAT texture cannot be exported with altered data.");
                return null;
            }
            if (!RegisterAssetInLunaJson(textureAssetPath))
            {
                Debug.LogError(
                    $"[VATBakeTool] Luna texture protection could not be verified for '{textureAssetPath}'. " +
                    "Bake stopped so this VAT texture cannot be exported with lossy settings.");
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(textureAssetPath);
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
                "• New — Create new asset files in the VATAssetDataSO-named output folder.\n" +
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
            Mesh existingMesh,
            bool overrideMode)
        {
            if (overrideMode && existingMesh != null)
            {
                CopyMeshData(existingMesh, bakedMesh);
                EditorUtility.SetDirty(existingMesh);
                DestroyImmediate(bakedMesh);
                return existingMesh;
            }

            string meshAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                Path.Combine(GetActiveOutputDirectory(), outputName + "_Static.asset").Replace('\\', '/'));
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
            importer.isReadable = false;
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
            List<Material> existingMaterials,
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

                if (overrideMode && existingMaterials != null &&
                    i < existingMaterials.Count &&
                    existingMaterials[i] != null)
                {
                    outputMaterial = existingMaterials[i];
                    outputMaterial.shader = vatShader;
                    outputMaterial.enableInstancing = true;
                    CopyBaseTextureAndTint(originalMaterial, outputMaterial);
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
                    outputMaterial.enableInstancing = true;
                    CopyBaseTextureAndTint(originalMaterial, outputMaterial);
                    outputMaterial.SetTexture(vatTextureId, vatTexture);
                    outputMaterial.SetVector(boundsMinId, boundsMin);
                    outputMaterial.SetVector(boundsMaxId, boundsMax);
                    outputMaterial.SetFloat(framesId, totalBakeFrames);
                    outputMaterial.SetFloat(verticesId, vertexCount);

                    string materialAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                        Path.Combine(GetActiveOutputDirectory(), outputMaterial.name + ".mat").Replace('\\', '/'));
                    AssetDatabase.CreateAsset(outputMaterial, materialAssetPath);
                    outputMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialAssetPath);
                }

                outputMaterials.Add(outputMaterial);
            }

            return outputMaterials;
        }

        private static bool EnsureMaterialBaseTexturesLunaProtected(List<Material> materials)
        {
            List<Texture2D> baseTextures = CollectMaterialBaseTextures(materials);
            for (int i = 0; i < baseTextures.Count; i++)
            {
                Texture2D texture = baseTextures[i];
                string textureAssetPath = AssetDatabase.GetAssetPath(texture);
                int requiredDimension = Mathf.Max(texture.width, texture.height);
                if (!RegisterTextureInLunaJson(textureAssetPath, requiredDimension))
                {
                    Debug.LogError(
                        $"[VATBakeTool] Bake stopped: base texture '{texture.name}' could not be protected in luna.json.");
                    return false;
                }
            }

            return true;
        }

        private static void CopyBaseTextureAndTint(Material sourceMaterial, Material destinationMaterial)
        {
            if (sourceMaterial == null || destinationMaterial == null || !destinationMaterial.HasProperty("_MainTex"))
            {
                return;
            }

            string sourceTextureProperty = sourceMaterial.HasProperty("_BaseMap") &&
                                           sourceMaterial.GetTexture("_BaseMap") != null
                ? "_BaseMap"
                : "_MainTex";
            Texture baseTexture = sourceMaterial.HasProperty(sourceTextureProperty)
                ? sourceMaterial.GetTexture(sourceTextureProperty)
                : null;
            if (baseTexture != null)
            {
                destinationMaterial.SetTexture("_MainTex", baseTexture);
                destinationMaterial.SetTextureScale("_MainTex", sourceMaterial.GetTextureScale(sourceTextureProperty));
                destinationMaterial.SetTextureOffset("_MainTex", sourceMaterial.GetTextureOffset(sourceTextureProperty));
            }

            if (destinationMaterial.HasProperty("_Color"))
            {
                Color tint = sourceMaterial.HasProperty("_BaseColor")
                    ? sourceMaterial.GetColor("_BaseColor")
                    : sourceMaterial.HasProperty("_Color")
                        ? sourceMaterial.GetColor("_Color")
                        : Color.white;
                destinationMaterial.SetColor("_Color", tint);
            }
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
            bool overrideMode,
            List<VATWeaponAssetEntry> weaponEntries)
        {
            VATAssetDataSO assetData = existingAsset;
            if (overrideMode && assetData != null)
            {
                assetData.BakedStaticMesh = outputMesh;
                assetData.VATTexture = outputTexture;
                if (assetData.BakedMaterials == null)
                {
                    assetData.BakedMaterials = new List<Material>();
                }
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
                assetData.BakedMaterials = new List<Material>();
                assetData.BakedMaterials.AddRange(outputMaterials);
                assetData.TotalVertices = vertexCount;
                assetData.TotalFrames = totalBakeFrames;
                assetData.BoundingMin = boundsMin;
                assetData.BoundingMax = boundsMax;

                string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.Combine(GetActiveOutputDirectory(), outputName + "Data.asset").Replace('\\', '/'));
                AssetDatabase.CreateAsset(assetData, assetPath);
            }

            if (assetData.Clips == null) assetData.Clips = new List<VATClipInfo>();
            if (assetData.Sockets == null) assetData.Sockets = new List<VATSocketTransformData>();
            SetClipManifest(assetData.Clips, clipsToBake, clipFramesList, _sampleFrameRate);
            if (assetData.WeaponAssets == null) assetData.WeaponAssets = new List<VATWeaponAssetEntry>();
            assetData.WeaponAssets.Clear();
            if (weaponEntries != null)
            {
                assetData.WeaponAssets.AddRange(weaponEntries);
            }
            assetData.DefaultWeaponAsset = assetData.WeaponAssets.Count > 0
                ? assetData.WeaponAssets[0].WeaponAsset
                : null;

            // Socket baking for VAT_ObjectMesh is intentionally disabled. Clear
            // legacy data when overriding an older VAT asset. Weapon motion is
            // now stored in the separate VATWeaponAssetSO channel.
            assetData.Sockets.Clear();
            EditorUtility.SetDirty(assetData);
            return assetData;
        }

        private VATWeaponAssetSO SaveVATWeaponAssetData(
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
            VATWeaponAssetSO existingAsset,
            bool overrideMode)
        {
            VATWeaponAssetSO assetData = existingAsset;
            if (overrideMode && assetData != null)
            {
                assetData.BakedStaticMesh = outputMesh;
                assetData.VATTexture = outputTexture;
                if (assetData.BakedMaterials == null)
                {
                    assetData.BakedMaterials = new List<Material>();
                }
                assetData.BakedMaterials.Clear();
                assetData.BakedMaterials.AddRange(outputMaterials);
            }
            else
            {
                assetData = ScriptableObject.CreateInstance<VATWeaponAssetSO>();
                assetData.BakedStaticMesh = outputMesh;
                assetData.VATTexture = outputTexture;
                assetData.BakedMaterials.AddRange(outputMaterials);

                string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.Combine(GetActiveOutputDirectory(), outputName + "Data.asset").Replace('\\', '/'));
                AssetDatabase.CreateAsset(assetData, assetPath);
            }

            assetData.BoundingMin = boundsMin;
            assetData.BoundingMax = boundsMax;
            assetData.TotalFrames = totalBakeFrames;
            assetData.TotalVertices = vertexCount;
            if (assetData.Clips == null) assetData.Clips = new List<VATClipInfo>();
            SetClipManifest(assetData.Clips, clipsToBake, clipFramesList, _sampleFrameRate);
            EditorUtility.SetDirty(assetData);
            return assetData;
        }

        private static void SetClipManifest(
            List<VATClipInfo> target,
            List<AnimationClip> clipsToBake,
            List<int> clipFramesList,
            int sampleRate)
        {
            if (target == null)
            {
                return;
            }

            target.Clear();
            int startFrame = 0;
            sampleRate = Mathf.Max(1, sampleRate);
            for (int i = 0; i < clipsToBake.Count; i++)
            {
                int endFrame = startFrame + clipFramesList[i] - 1;
                target.Add(new VATClipInfo
                {
                    ClipName = clipsToBake[i].name,
                    StateHash = VATClipInfo.GenerateHash(clipsToBake[i].name),
                    StartFrame = startFrame,
                    EndFrame = endFrame,
                    FrameRate = sampleRate,
                    IsLooping = true
                });
                startFrame = endFrame + 1;
            }
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
            string bodyMeshPath = Path.Combine(_savePath, _targetPrefab.name + "_VAT_Body_Static.asset");
            string bodyTexPath = Path.Combine(_savePath, _targetPrefab.name + "_VAT_Body_Texture.png");
            string weaponSoPath = Path.Combine(_savePath, _targetPrefab.name + "_VAT_WeaponData.asset");

            return File.Exists(meshPath) || File.Exists(texPath) || File.Exists(soPath) ||
                   File.Exists(bodyMeshPath) || File.Exists(bodyTexPath) || File.Exists(weaponSoPath);
        }

        private static bool RegisterAssetInLunaJson(string assetPath)
        {
            return RegisterTextureInLunaJson(assetPath, MaxVATTextureDimension);
        }

        private static bool RegisterTextureInLunaJson(string assetPath, int requiredDimension)
        {
            if (string.IsNullOrEmpty(assetPath) || requiredDimension <= 0 || requiredDimension > MaxVATTextureDimension)
            {
                return false;
            }

            string lunaJsonPath = GetLunaJsonPath();
            if (!File.Exists(lunaJsonPath))
            {
                Debug.LogError("[VATBakeTool] luna.json was not found. VAT texture protection cannot be applied.");
                return false;
            }

            try
            {
                string jsonText = File.ReadAllText(lunaJsonPath);
                string normalizedPath = assetPath.Replace('\\', '/');
                bool changed = false;

                if (!TryEnsureLunaAssetInclude(ref jsonText, normalizedPath, ref changed) ||
                    !TryEnsureLunaTextureOverride(ref jsonText, normalizedPath, requiredDimension, ref changed))
                {
                    Debug.LogError(
                        $"[VATBakeTool] Could not locate the Luna asset include or texture override sections for '{normalizedPath}'.");
                    return false;
                }

                if (changed)
                {
                    File.WriteAllText(lunaJsonPath, jsonText);
                    Debug.Log($"[VATBakeTool] Protected '{normalizedPath}' in luna.json for VAT export.");
                }

                LunaTextureOverrideSettings lunaSettings;
                bool isProtected = TryGetLunaTextureOverride(normalizedPath, out lunaSettings) &&
                                   IsLunaTextureProtected(lunaSettings, requiredDimension);
                if (!isProtected)
                {
                    Debug.LogError(
                        $"[VATBakeTool] Luna override verification failed for '{normalizedPath}'. " +
                        $"Expected PNG32, no compression and at least a {requiredDimension} x {requiredDimension} limit.");
                }

                return isProtected;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VATBakeTool] Failed to protect VAT texture in luna.json: {ex.Message}");
                return false;
            }
        }

        private static bool TryEnsureLunaAssetInclude(ref string jsonText, string assetPath, ref bool changed)
        {
            int unityStart;
            int unityEnd;
            int assetsStart;
            int assetsEnd;
            int includesStart;
            int includesEnd;
            if (!TryFindJsonObjectProperty(jsonText, "unity", 0, jsonText.Length, out unityStart, out unityEnd) ||
                !TryFindJsonObjectProperty(jsonText, "assets", unityStart + 1, unityEnd, out assetsStart, out assetsEnd) ||
                !TryFindJsonArrayProperty(jsonText, "includes", assetsStart + 1, assetsEnd, out includesStart, out includesEnd))
            {
                return false;
            }

            if (JsonArrayContainsString(jsonText, includesStart, includesEnd, assetPath))
            {
                return true;
            }

            AppendJsonArrayString(ref jsonText, includesStart, includesEnd, assetPath);
            changed = true;
            return true;
        }

        private static bool TryEnsureLunaTextureOverride(
            ref string jsonText,
            string assetPath,
            int requiredDimension,
            ref bool changed)
        {
            int textureStart;
            int textureEnd;
            int overridesStart;
            int overridesEnd;
            if (!TryFindLunaTextureRulesObject(jsonText, out textureStart, out textureEnd) ||
                !TryFindJsonArrayProperty(jsonText, "overrides", textureStart + 1, textureEnd, out overridesStart, out overridesEnd))
            {
                return false;
            }

            string newLine = GetJsonNewLine(jsonText);
            int existingOverrideStart;
            int existingOverrideEnd;
            if (TryFindLunaTextureOverrideObject(
                    jsonText,
                    overridesStart,
                    overridesEnd,
                    assetPath,
                    out existingOverrideStart,
                    out existingOverrideEnd))
            {
                string existingOverride = jsonText.Substring(
                    existingOverrideStart,
                    existingOverrideEnd - existingOverrideStart + 1);
                string replacement = BuildLunaTextureOverride(
                    assetPath,
                    requiredDimension,
                    GetJsonLineIndentation(jsonText, existingOverrideStart),
                    newLine);
                if (!string.Equals(existingOverride, replacement, StringComparison.Ordinal))
                {
                    jsonText = jsonText.Remove(existingOverrideStart, existingOverride.Length)
                                       .Insert(existingOverrideStart, replacement);
                    changed = true;
                }

                return true;
            }

            string objectIndent = GetJsonLineIndentation(jsonText, overridesStart) + "    ";
            string overrideEntry = BuildLunaTextureOverride(assetPath, requiredDimension, objectIndent, newLine);
            bool hasExistingEntries = JsonArrayHasValues(jsonText, overridesStart, overridesEnd);
            int insertPosition = GetJsonLineStart(jsonText, overridesEnd);
            string insertion;
            if (hasExistingEntries)
            {
                insertPosition = MoveBeforePreviousNewLine(jsonText, insertPosition);
                insertion = "," + newLine + objectIndent + overrideEntry;
            }
            else
            {
                insertion = objectIndent + overrideEntry + newLine;
            }

            jsonText = jsonText.Insert(insertPosition, insertion);
            changed = true;
            return true;
        }

        private static bool TryGetLunaTextureOverride(string assetPath, out LunaTextureOverrideSettings settings)
        {
            settings = new LunaTextureOverrideSettings();
            if (string.IsNullOrEmpty(assetPath)) return false;

            string lunaJsonPath = GetLunaJsonPath();
            if (!File.Exists(lunaJsonPath)) return false;

            try
            {
                JObject root = JObject.Parse(File.ReadAllText(lunaJsonPath));
                JObject textureRules = root["assets"]?["rules"]?["texture"] as JObject;
                JArray overrides = textureRules?["overrides"] as JArray;
                if (overrides == null) return false;

                string normalizedPath = assetPath.Replace('\\', '/');
                for (int i = 0; i < overrides.Count; i++)
                {
                    JObject candidate = overrides[i] as JObject;
                    if (candidate == null ||
                        !string.Equals((string)candidate["name"], normalizedPath, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    settings.Exists = true;
                    settings.MaxWidth = candidate.Value<int?>("maxWidth") ?? 0;
                    settings.MaxHeight = candidate.Value<int?>("maxHeight") ?? 0;
                    settings.Format = candidate.Value<string>("format") ?? string.Empty;
                    settings.Compression = candidate.Value<string>("compression") ?? string.Empty;
                    settings.Quality = candidate.Value<int?>("quality") ?? 0;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VATBakeTool] Failed to read Luna texture rules: {ex.Message}");
            }

            return false;
        }

        private static string GetLunaJsonPath()
        {
            DirectoryInfo projectRoot = Directory.GetParent(Application.dataPath);
            return projectRoot != null
                ? Path.Combine(projectRoot.FullName, "luna.json")
                : Path.Combine(Directory.GetCurrentDirectory(), "luna.json");
        }

        private static bool IsLunaTextureProtected(LunaTextureOverrideSettings settings, int requiredDimension)
        {
            return settings.Exists &&
                   settings.MaxWidth >= requiredDimension &&
                   settings.MaxHeight >= requiredDimension &&
                   string.Equals(settings.Format, LunaVATTextureFormat, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(settings.Compression, LunaVATTextureCompression, StringComparison.OrdinalIgnoreCase) &&
                   settings.Quality >= LunaVATTextureQuality;
        }

        private static bool TryFindLunaTextureRulesObject(string jsonText, out int textureStart, out int textureEnd)
        {
            textureStart = -1;
            textureEnd = -1;
            int searchIndex = 0;
            int assetsStart;
            int assetsEnd;
            while (TryFindJsonObjectProperty(jsonText, "assets", searchIndex, jsonText.Length, out assetsStart, out assetsEnd))
            {
                int rulesStart;
                int rulesEnd;
                if (TryFindJsonObjectProperty(jsonText, "rules", assetsStart + 1, assetsEnd, out rulesStart, out rulesEnd) &&
                    TryFindJsonObjectProperty(jsonText, "texture", rulesStart + 1, rulesEnd, out textureStart, out textureEnd))
                {
                    return true;
                }

                searchIndex = assetsEnd + 1;
            }

            return false;
        }

        private static bool TryFindLunaTextureOverrideObject(
            string jsonText,
            int overridesStart,
            int overridesEnd,
            string assetPath,
            out int objectStart,
            out int objectEnd)
        {
            objectStart = -1;
            objectEnd = -1;
            string nameProperty = $"\"name\": \"{assetPath}\"";
            int searchIndex = overridesStart + 1;
            while (searchIndex < overridesEnd)
            {
                int nameIndex = jsonText.IndexOf(nameProperty, searchIndex, StringComparison.Ordinal);
                if (nameIndex < 0 || nameIndex >= overridesEnd) return false;

                int candidateStart = jsonText.LastIndexOf('{', nameIndex);
                int candidateEnd = candidateStart >= 0
                    ? FindMatchingJsonDelimiter(jsonText, candidateStart, '{', '}')
                    : -1;
                if (candidateStart > overridesStart && candidateEnd > candidateStart && candidateEnd < overridesEnd)
                {
                    objectStart = candidateStart;
                    objectEnd = candidateEnd;
                    return true;
                }

                searchIndex = nameIndex + nameProperty.Length;
            }

            return false;
        }

        private static string BuildLunaTextureOverride(
            string assetPath,
            int requiredDimension,
            string objectIndent,
            string newLine)
        {
            string propertyIndent = objectIndent + "    ";
            return "{" + newLine +
                   propertyIndent + "\"maxWidth\": " + requiredDimension + "," + newLine +
                   propertyIndent + "\"maxHeight\": " + requiredDimension + "," + newLine +
                   propertyIndent + "\"format\": \"" + LunaVATTextureFormat + "\"," + newLine +
                   propertyIndent + "\"compression\": \"" + LunaVATTextureCompression + "\"," + newLine +
                   propertyIndent + "\"quality\": " + LunaVATTextureQuality + "," + newLine +
                   propertyIndent + "\"script\": \"\"," + newLine +
                   propertyIndent + "\"ext\": \"\"," + newLine +
                   propertyIndent + "\"name\": \"" + assetPath + "\"" + newLine +
                   objectIndent + "}";
        }

        private static bool TryFindJsonObjectProperty(
            string jsonText,
            string propertyName,
            int searchStart,
            int searchEnd,
            out int objectStart,
            out int objectEnd)
        {
            return TryFindJsonCollectionProperty(
                jsonText,
                propertyName,
                searchStart,
                searchEnd,
                '{',
                '}',
                out objectStart,
                out objectEnd);
        }

        private static bool TryFindJsonArrayProperty(
            string jsonText,
            string propertyName,
            int searchStart,
            int searchEnd,
            out int arrayStart,
            out int arrayEnd)
        {
            return TryFindJsonCollectionProperty(
                jsonText,
                propertyName,
                searchStart,
                searchEnd,
                '[',
                ']',
                out arrayStart,
                out arrayEnd);
        }

        private static bool TryFindJsonCollectionProperty(
            string jsonText,
            string propertyName,
            int searchStart,
            int searchEnd,
            char openingDelimiter,
            char closingDelimiter,
            out int valueStart,
            out int valueEnd)
        {
            valueStart = -1;
            valueEnd = -1;
            string propertyToken = "\"" + propertyName + "\"";
            int searchIndex = searchStart;
            while (searchIndex < searchEnd)
            {
                int propertyIndex = jsonText.IndexOf(propertyToken, searchIndex, StringComparison.Ordinal);
                if (propertyIndex < 0 || propertyIndex >= searchEnd) return false;

                int valueIndex = propertyIndex + propertyToken.Length;
                while (valueIndex < searchEnd && char.IsWhiteSpace(jsonText[valueIndex])) valueIndex++;
                if (valueIndex >= searchEnd || jsonText[valueIndex] != ':')
                {
                    searchIndex = propertyIndex + propertyToken.Length;
                    continue;
                }

                valueIndex++;
                while (valueIndex < searchEnd && char.IsWhiteSpace(jsonText[valueIndex])) valueIndex++;
                if (valueIndex < searchEnd && jsonText[valueIndex] == openingDelimiter)
                {
                    int matchingIndex = FindMatchingJsonDelimiter(
                        jsonText,
                        valueIndex,
                        openingDelimiter,
                        closingDelimiter);
                    if (matchingIndex > valueIndex && matchingIndex <= searchEnd)
                    {
                        valueStart = valueIndex;
                        valueEnd = matchingIndex;
                        return true;
                    }
                }

                searchIndex = propertyIndex + propertyToken.Length;
            }

            return false;
        }

        private static int FindMatchingJsonDelimiter(string jsonText, int openingIndex, char openingDelimiter, char closingDelimiter)
        {
            int depth = 0;
            bool insideString = false;
            bool escaped = false;
            for (int i = openingIndex; i < jsonText.Length; i++)
            {
                char character = jsonText[i];
                if (insideString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        insideString = false;
                    }

                    continue;
                }

                if (character == '"')
                {
                    insideString = true;
                }
                else if (character == openingDelimiter)
                {
                    depth++;
                }
                else if (character == closingDelimiter)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }

            return -1;
        }

        private static bool JsonArrayContainsString(string jsonText, int arrayStart, int arrayEnd, string value)
        {
            int valueIndex = jsonText.IndexOf("\"" + value + "\"", arrayStart + 1, StringComparison.Ordinal);
            return valueIndex >= 0 && valueIndex < arrayEnd;
        }

        private static bool JsonArrayHasValues(string jsonText, int arrayStart, int arrayEnd)
        {
            for (int i = arrayStart + 1; i < arrayEnd; i++)
            {
                if (!char.IsWhiteSpace(jsonText[i])) return true;
            }

            return false;
        }

        private static void AppendJsonArrayString(ref string jsonText, int arrayStart, int arrayEnd, string value)
        {
            string newLine = GetJsonNewLine(jsonText);
            string entryIndent = GetJsonLineIndentation(jsonText, arrayStart) + "    ";
            int insertPosition = GetJsonLineStart(jsonText, arrayEnd);
            bool hasExistingEntries = JsonArrayHasValues(jsonText, arrayStart, arrayEnd);
            string insertion;
            if (hasExistingEntries)
            {
                insertPosition = MoveBeforePreviousNewLine(jsonText, insertPosition);
                insertion = "," + newLine + entryIndent + "\"" + value + "\"";
            }
            else
            {
                insertion = entryIndent + "\"" + value + "\"" + newLine;
            }

            jsonText = jsonText.Insert(insertPosition, insertion);
        }

        private static string GetJsonNewLine(string jsonText)
        {
            return jsonText.Contains("\r\n") ? "\r\n" : "\n";
        }

        private static string GetJsonLineIndentation(string jsonText, int index)
        {
            int lineStart = GetJsonLineStart(jsonText, index);
            int indentEnd = lineStart;
            while (indentEnd < jsonText.Length &&
                   (jsonText[indentEnd] == ' ' || jsonText[indentEnd] == '\t'))
            {
                indentEnd++;
            }

            return jsonText.Substring(lineStart, indentEnd - lineStart);
        }

        private static int GetJsonLineStart(string jsonText, int index)
        {
            int newLineIndex = jsonText.LastIndexOf('\n', Mathf.Max(0, index - 1));
            return newLineIndex >= 0 ? newLineIndex + 1 : 0;
        }

        private static int MoveBeforePreviousNewLine(string jsonText, int lineStart)
        {
            int insertionPosition = lineStart;
            if (insertionPosition > 0 && jsonText[insertionPosition - 1] == '\n')
            {
                insertionPosition--;
                if (insertionPosition > 0 && jsonText[insertionPosition - 1] == '\r')
                {
                    insertionPosition--;
                }
            }

            return insertionPosition;
        }
    }
}
