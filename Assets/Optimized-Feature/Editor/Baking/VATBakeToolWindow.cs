using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
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
        // Controls material validation within each Body/Item VAT channel.
        PerSkinnedMesh,
        Combined
    }

    public enum VATRendererRole
    {
        Body,
        Item
    }

    // These profiles apply to visual material base textures only. VAT position
    // textures use their own lossless RGB24/RGBA32 profiles declared below.
    public enum LunaVisualTextureFormat
    {
        PNG32,
        PNG24,
        PNG8,
        JPEG,
        Webp
    }

    public enum VATPositionTextureStorage
    {
        RGBA32,
        RGB24
    }

    /// <summary>
    /// Editor Tool Window implementing the VAT Bake process with automatic references discovery,
    /// selective animation clip baking and Material/Shader validation displays.
    /// </summary>
    public class VATBakeToolWindow : EditorWindow
    {
        // Temporary compatibility mode: all VAT samples are generated with the
        // complete model-parent chain normalized to (1,1,1). The authored
        // scale is stored in VATAssetDataSO and restored during Runtime Setup.
        private const bool NormalizeModelScaleDuringBake = true;

        // VAT data uses one texel per vertex/frame. This is the hard ceiling
        // for a generated layout; exceeding it would resize the asset and
        // invalidate the vertex-to-texel mapping.
        private const int MaxVATTextureDimension = 4096;
        private const string LunaVATTextureFormat = "png32";
        private const string LunaVATTextureCompression = "none";
        private const int LunaVATTextureQuality = 100;
        private const float SectionTabHeight = 24f;
        private const float CommonFooterHeight = 210f;
        private const float VATFrameValidationMagnitudeMultiplier = 1000f;
        private const float VATFrameValidationMinimumMagnitude = 10000f;

        [Header("Baking Settings")]
        private string _savePath = "Assets/Optimized-Feature/BakedAssets/";
        private int _sampleFrameRate = 30;
        private ShaderPatchMode _shaderPatchMode = ShaderPatchMode.AutoPatchIfMissing;
        private VATBakeOutputMode _outputMode = VATBakeOutputMode.PerSkinnedMesh;
        [SerializeField] private bool _enableOutline = false;
        [SerializeField]
        private VATPositionTextureStorage _vatPositionTextureStorage =
            VATPositionTextureStorage.RGBA32;

        [Header("Baking Input Data")]
        private GameObject _targetPrefab;
        private Transform _vatBakeRoot;
        // Metadata captured for VATAssetDataSO only. This value must never be
        // used as a bake transform; VAT geometry is always baked in normalized
        // common-parent space.
        private Vector3 _capturedModelScale = Vector3.one;
        private readonly List<Transform> _normalizedBakeScaleTransforms = new List<Transform>();
        private readonly List<Vector3> _originalBakeScaleValues = new List<Vector3>();
        [SerializeField] private string _outputName;
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
        [SerializeField] private LunaVisualTextureFormat _baseTextureLunaFormat = LunaVisualTextureFormat.PNG32;
        private string _activeOutputDirectory;
        private string _cachedLunaTexturePath;
        private DateTime _cachedLunaJsonWriteTimeUtc = DateTime.MinValue;
        private bool _cachedLunaOverrideFound;
        private LunaTextureOverrideSettings _cachedLunaOverrideSettings;
        private LunaTextureSetupBatch _activeLunaTextureSetupBatch;

        [Header("Runtime Setup Data")]
        [SerializeField] private List<GameObject> _setupTargetRoots = new List<GameObject>();
        [SerializeField] private VATAssetDataSO _setupVATAssetData;
        [SerializeField] private Material _setupVATMaterial;
        [SerializeField, FormerlySerializedAs("_setupDefaultWeaponIndex")]
        private int _setupDefaultItemIndex = -1;
        private SerializedObject _setupSerializedObject;
        private SerializedProperty _setupTargetRootsProperty;

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
            public Mesh PoseMesh;
            public List<Vector3> PoseVertices = new List<Vector3>();
            public Matrix4x4 RendererReferenceWorld;
            public Transform RigidBone;
            public Matrix4x4 RigidBoneReferenceWorld;
            public int RigidBoneIndex = -1;
            public bool UseManualRigidSkinning;
            public bool UseRigidReferencePoseFallback;
            public float RigidBakeValidationError;
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

        // Keep the bake tool independent from the Newtonsoft package. JsonUtility
        // is sufficient for the small, stable luna.json branch that we inspect.
#pragma warning disable 0649
        [Serializable]
        private sealed class LunaJsonDocument
        {
            public LunaJsonAssets assets;
        }

        [Serializable]
        private sealed class LunaJsonAssets
        {
            public LunaJsonRules rules;
        }

        [Serializable]
        private sealed class LunaJsonRules
        {
            public LunaJsonTextureRules texture;
        }

        [Serializable]
        private sealed class LunaJsonTextureRules
        {
            public LunaJsonTextureOverride[] overrides;
        }

        [Serializable]
        private sealed class LunaJsonTextureOverride
        {
            public int maxWidth;
            public int maxHeight;
            public string format;
            public string compression;
            public int quality;
            public string name;
        }
#pragma warning restore 0649

        private struct LunaTextureExportSettings
        {
            public string Format;
            public string Compression;
            public int Quality;
        }

        private sealed class LunaTextureSetupRequest
        {
            public string AssetPath;
            public int RequiredWidth;
            public int RequiredHeight;
            public LunaTextureExportSettings ExportSettings;
        }

        private sealed class LunaTextureSetupBatch
        {
            private readonly Dictionary<string, LunaTextureSetupRequest> _requests =
                new Dictionary<string, LunaTextureSetupRequest>(StringComparer.Ordinal);

            public int Count => _requests.Count;
            public IEnumerable<LunaTextureSetupRequest> Requests => _requests.Values;

            public bool Queue(
                string assetPath,
                int requiredWidth,
                int requiredHeight,
                LunaTextureExportSettings exportSettings)
            {
                if (string.IsNullOrEmpty(assetPath) || requiredWidth <= 0 || requiredHeight <= 0 ||
                    requiredWidth > MaxVATTextureDimension || requiredHeight > MaxVATTextureDimension)
                {
                    return false;
                }

                string normalizedPath = assetPath.Replace('\\', '/');
                _requests[normalizedPath] = new LunaTextureSetupRequest
                {
                    AssetPath = normalizedPath,
                    RequiredWidth = requiredWidth,
                    RequiredHeight = requiredHeight,
                    ExportSettings = exportSettings
                };
                return true;
            }
        }

        private struct VATTextureLayout
        {
            public int Width;
            public int Height;
            public int RequiredImporterSize;
            public bool UsesPackedRows;
        }

        [MenuItem("Tools/VAT/VAT Bake Tool", priority = 100)]
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
            _enableOutline = EditorGUILayout.Toggle("Enable Outline (Baked Output)", _enableOutline);
            EditorGUILayout.HelpBox(
                _enableOutline
                    ? "The next bake enables the OUTLINE material keyword on generated Body and Item materials."
                    : "The next bake disables the OUTLINE material keyword and resets generated materials to no outline.",
                MessageType.None);
            _vatPositionTextureStorage = (VATPositionTextureStorage)EditorGUILayout.EnumPopup(
                "VAT Position Storage",
                _vatPositionTextureStorage);
            if (_outputMode == VATBakeOutputMode.PerSkinnedMesh)
            {
                EditorGUILayout.HelpBox(
                    "Per SkinnedMesh allows different source shaders and base textures. " +
                    "The result uses a Body VAT channel and an optional Item VAT sub-render channel.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Combined requires every material slot to share the same Shader and BaseTexture.",
                    MessageType.Info);
            }
            EditorGUILayout.HelpBox(
                "Assign each detected renderer to Body or Item. Both channels are sampled from the same clip/frame manifest; Item is optional.",
                MessageType.Info);
            EditorGUILayout.HelpBox(
                _vatPositionTextureStorage == VATPositionTextureStorage.RGB24
                    ? "RGB24 is lossless for this VAT shader because it samples RGB only. It removes the unused alpha channel to reduce PNG payload size; it does not resize the texture."
                    : "RGBA32 is the maximum-compatibility VAT profile. The unused alpha channel is retained.",
                MessageType.Info);
            _baseTextureLunaFormat = (LunaVisualTextureFormat)EditorGUILayout.EnumPopup(
                "Base Texture Luna Format",
                _baseTextureLunaFormat);
            LunaTextureExportSettings baseTextureSettings = GetVisualTextureExportSettings(_baseTextureLunaFormat);
            EditorGUILayout.HelpBox(
                $"The Base Texture profile ({baseTextureSettings.Format} / {baseTextureSettings.Compression} / Quality {baseTextureSettings.Quality}) is used only for visual material textures during the next bake. VAT position data always stays lossless and uses the VAT Position Storage setting above.",
                MessageType.None);
            if (_baseTextureLunaFormat == LunaVisualTextureFormat.PNG24)
            {
                EditorGUILayout.HelpBox(
                    "PNG24 removes alpha. Use it only when every baked Base Texture is fully opaque.",
                    MessageType.Warning);
            }
            else if (_baseTextureLunaFormat == LunaVisualTextureFormat.PNG8 ||
                     _baseTextureLunaFormat == LunaVisualTextureFormat.JPEG ||
                     _baseTextureLunaFormat == LunaVisualTextureFormat.Webp)
            {
                EditorGUILayout.HelpBox(
                    "This visual Base Texture profile can change color or alpha data. It is never used for VAT position textures.",
                    MessageType.Warning);
            }
            EditorGUILayout.HelpBox(
                "VAT layout is calculated automatically from the baked vertex and frame counts. If the one-frame-per-row layout exceeds 4096 in either direction, data is packed into a valid 4096 x 4096 texture without dropping frames or vertices.",
                MessageType.None);
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
                EditorGUILayout.LabelField("Resolved Output Root", GetConfiguredOutputName());
            }

            if (_targetPrefab != null)
            {
                SyncMeshSelectionToggles();
                int activeMeshCount = GetSelectedMeshCount();

                EditorGUILayout.HelpBox(
                    "Item Render Name is the bake grouping key. Matching names bake into one Item VAT asset. After baking, the tool derives its stable runtime item hash from that name; no hash is entered in Inputs.",
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
                        if (_meshRoles[i] == VATRendererRole.Item)
                        {
                            _weaponOutputNames[i] = EditorGUILayout.TextField(
                                new GUIContent("", "Bake grouping and Editor-only item render/output name. The runtime item hash is generated from this value after baking."),
                                _weaponOutputNames[i],
                                GUILayout.Width(180));
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
            if (_targetPrefab != null)
            {
                string resolvedOutputName = GetConfiguredOutputName();
                EditorGUILayout.LabelField("Resolved Output Root", resolvedOutputName);
                VATAssetDataSO matchingOutput = GetExistingOutputAsset(resolvedOutputName);
                EditorGUILayout.HelpBox(
                    matchingOutput == _outputAssetData
                        ? $"Bake will update the existing output '{_outputAssetData.name}' after confirmation."
                        : $"Bake will create a new output using '{resolvedOutputName}'. The selected asset is preview-only for this name.",
                    matchingOutput == _outputAssetData ? MessageType.Warning : MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"Bake will update the selected output '{_outputAssetData.name}' after confirmation.",
                    MessageType.Warning);
            }
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
                "VAT Texture Bake Information & Luna Protection",
                true);
            if (_vatTextureQualityFoldout)
            {
                DrawVATTextureQualityPreview(
                    "Body",
                    _outputAssetData.VATTexture,
                    _outputAssetData.TotalVertices,
                    _outputAssetData.TotalFrames);
                if (_outputAssetData.WeaponAssets != null)
                {
                    for (int i = 0; i < _outputAssetData.WeaponAssets.Count; i++)
                    {
                        VATWeaponAssetEntry entry = _outputAssetData.WeaponAssets[i];
                        VATWeaponAssetSO weaponAsset = entry != null ? entry.WeaponAsset : null;
                        if (weaponAsset != null)
                        {
                            DrawVATTextureQualityPreview(
                                string.IsNullOrEmpty(entry.WeaponName) ? "Item" : entry.WeaponName,
                                weaponAsset.VATTexture,
                                weaponAsset.TotalVertices,
                                weaponAsset.TotalFrames);
                        }
                    }
                }
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

            DrawSetupItemSelection();

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
            VATWeaponAssetEntry selectedItemEntry = GetSetupItemAssetEntry(
                _setupVATAssetData,
                _setupDefaultItemIndex);
            bool hasValidItemSelection = _setupDefaultItemIndex == -1 ||
                                         (selectedItemEntry != null && selectedItemEntry.WeaponAsset != null);
            bool hasCompatibleItemSelection = _setupDefaultItemIndex == -1 ||
                                               (hasValidItemSelection &&
                                                HasMatchingItemFrameManifest(
                                                     _setupVATAssetData,
                                                     selectedItemEntry.WeaponAsset));
            bool canSetup = validTargetCount > 0 && _setupVATAssetData != null &&
                            hasVATMaterial && hasValidItemSelection &&
                            hasCompatibleItemSelection;

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
            else if (!hasValidItemSelection)
            {
                EditorGUILayout.HelpBox(
                    "Select a valid Default Item in the Runtime Setup tab, or clear the item toggle to skip item loading.",
                    MessageType.Warning);
            }
            else if (!hasCompatibleItemSelection)
            {
                EditorGUILayout.HelpBox(
                    "The selected item VAT does not match the Body frame manifest. " +
                    "Rebake that item with the same clips/FPS, or clear the item toggle.",
                    MessageType.Error);
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
                    _setupDefaultItemIndex);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndVertical();
        }

        private void DrawSetupItemSelection()
        {
            EditorGUILayout.LabelField("Default Item", EditorStyles.boldLabel);

            int itemCount = GetSetupItemAssetCount(_setupVATAssetData);
            if (_setupVATAssetData == null || itemCount == 0)
            {
                _setupDefaultItemIndex = -1;
                EditorGUILayout.HelpBox(
                    "[No Item Found] No item VAT asset is available. Item loading is disabled (-1).",
                    MessageType.Info);
                return;
            }

            if (_setupDefaultItemIndex < 0 || _setupDefaultItemIndex >= itemCount)
            {
                _setupDefaultItemIndex = -1;
            }

            EditorGUILayout.HelpBox(
                "Select one item to load during Runtime Setup. Clear the selected toggle to use -1 and skip item loading.",
                MessageType.Info);
            for (int i = 0; i < itemCount; i++)
            {
                VATWeaponAssetEntry entry = GetSetupItemAssetEntry(_setupVATAssetData, i);
                string itemName = GetSetupItemName(entry);
                bool isSelected = _setupDefaultItemIndex == i;
                bool nextSelected = EditorGUILayout.ToggleLeft(
                    $"Item [{i}] {itemName}",
                    isSelected);
                if (nextSelected && !isSelected)
                {
                    _setupDefaultItemIndex = i;
                }
                else if (!nextSelected && isSelected)
                {
                    _setupDefaultItemIndex = -1;
                }
            }

            VATWeaponAssetEntry selectedEntry = GetSetupItemAssetEntry(
                _setupVATAssetData,
                _setupDefaultItemIndex);
            if (_setupDefaultItemIndex == -1)
            {
                EditorGUILayout.HelpBox(
                    "No item selected (-1). Runtime Setup will not create or load an item sub-render.",
                    MessageType.Info);
            }
            else if (selectedEntry == null || selectedEntry.WeaponAsset == null)
            {
                EditorGUILayout.HelpBox(
                    $"Item [{_setupDefaultItemIndex}] '{GetSetupItemName(selectedEntry)}' has no baked asset.",
                    MessageType.Warning);
            }
            else if (!HasMatchingItemFrameManifest(_setupVATAssetData, selectedEntry.WeaponAsset))
            {
                EditorGUILayout.HelpBox(
                    $"Item [{_setupDefaultItemIndex}] '{GetSetupItemName(selectedEntry)}' has an invalid frame manifest. " +
                    "Rebake this item with the same clips/FPS as Body or clear the item toggle.",
                    MessageType.Error);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"Item [{_setupDefaultItemIndex}] '{GetSetupItemName(selectedEntry)}' will be loaded during Runtime Setup.",
                    MessageType.Info);
            }
        }

        private static bool HasMatchingItemFrameManifest(
            VATAssetDataSO bodyAsset,
            VATWeaponAssetSO itemAsset)
        {
            if (bodyAsset == null || itemAsset == null ||
                bodyAsset.TotalFrames <= 0 ||
                itemAsset.TotalFrames != bodyAsset.TotalFrames ||
                bodyAsset.Clips == null || itemAsset.Clips == null ||
                itemAsset.Clips.Count != bodyAsset.Clips.Count)
            {
                return false;
            }

            for (int i = 0; i < bodyAsset.Clips.Count; i++)
            {
                VATClipInfo bodyClip = bodyAsset.Clips[i];
                VATClipInfo itemClip = itemAsset.Clips[i];
                if (bodyClip == null || itemClip == null ||
                    bodyClip.StateHash != itemClip.StateHash ||
                    bodyClip.StartFrame != itemClip.StartFrame ||
                    bodyClip.EndFrame != itemClip.EndFrame ||
                    !Mathf.Approximately(bodyClip.FrameRate, itemClip.FrameRate))
                {
                    return false;
                }
            }

            return true;
        }

        private static int GetSetupItemAssetCount(VATAssetDataSO assetData)
        {
            if (assetData == null) return 0;
            if (assetData.WeaponAssets != null && assetData.WeaponAssets.Count > 0)
            {
                return assetData.WeaponAssets.Count;
            }

            return assetData.DefaultWeaponAsset != null ? 1 : 0;
        }

        private static VATWeaponAssetEntry GetSetupItemAssetEntry(VATAssetDataSO assetData, int itemIndex)
        {
            if (assetData == null || itemIndex < 0) return null;
            if (assetData.WeaponAssets != null && itemIndex < assetData.WeaponAssets.Count)
            {
                return assetData.WeaponAssets[itemIndex];
            }

            return itemIndex == 0 && assetData.DefaultWeaponAsset != null
                ? new VATWeaponAssetEntry
                {
                    WeaponName = "Item",
                    WeaponHash = VATWeaponAssetEntry.DefaultItemHash,
                    WeaponAsset = assetData.DefaultWeaponAsset
                }
                : null;
        }

        private static string GetSetupItemName(VATWeaponAssetEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.WeaponName))
            {
                return "[No Item Found]";
            }

            return entry.WeaponName.Trim();
        }

        private void InitializeSetupSerializedProperties()
        {
            if (_setupSerializedObject != null &&
                _setupSerializedObject.targetObject == this &&
                _setupTargetRootsProperty != null)
            {
                return;
            }

            if (_setupTargetRoots == null) _setupTargetRoots = new List<GameObject>();

            _setupSerializedObject = new SerializedObject(this);
            _setupTargetRootsProperty = _setupSerializedObject.FindProperty("_setupTargetRoots");
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

            bool hasMatchingOutput = isReadyToBake &&
                                     GetExistingOutputAsset(GetConfiguredOutputName()) != null;
            string buttonLabel = hasMatchingOutput
                ? "Bake VAT Assets..."
                : "Simulate VAT Baking Pipeline";
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
                _weaponOutputNames.Add("Item");
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

            int itemAssetCount = assetData.WeaponAssets != null ? assetData.WeaponAssets.Count : 0;
            EditorGUILayout.LabelField("Item VAT Channels", itemAssetCount.ToString());
            for (int i = 0; i < itemAssetCount; i++)
            {
                VATWeaponAssetEntry entry = assetData.WeaponAssets[i];
                VATWeaponAssetSO itemAsset = entry != null ? entry.WeaponAsset : null;
                string itemName = entry == null || string.IsNullOrWhiteSpace(entry.WeaponName)
                    ? "Item"
                    : entry.WeaponName;
                int itemHash = entry == null ? 0 : entry.WeaponHash;
                EditorGUILayout.ObjectField($"Item [{itemName}] (Hash {itemHash})", itemAsset, typeof(VATWeaponAssetSO), false);
                if (itemAsset != null)
                {
                    EditorGUILayout.IntField($"{itemName} Vertices", itemAsset.TotalVertices);
                    EditorGUILayout.IntField($"{itemName} Frames", itemAsset.TotalFrames);
                }
            }

            if (itemAssetCount == 0 && assetData.DefaultWeaponAsset != null)
            {
                EditorGUILayout.ObjectField(
                    "Legacy Default Item",
                    assetData.DefaultWeaponAsset,
                    typeof(VATWeaponAssetSO),
                    false);
            }

            EditorGUI.EndDisabledGroup();
        }

        private void DrawVATTextureQualityPreview(
            string channelLabel,
            Texture2D vatTexture,
            int totalVertices,
            int totalFrames)
        {
            EditorGUILayout.Space();

            if (vatTexture == null)
            {
                EditorGUILayout.HelpBox($"No VAT texture is assigned to the {channelLabel} channel.", MessageType.Error);
                return;
            }

            string textureAssetPath = AssetDatabase.GetAssetPath(vatTexture);
            TextureImporter importer = AssetImporter.GetAtPath(textureAssetPath) as TextureImporter;
            VATTextureLayout layout = CreateVATTextureLayout(
                vatTexture.width,
                vatTexture.height,
                vatTexture.width != totalVertices || vatTexture.height != totalFrames);
            VATPositionTextureStorage storage = GetVATPositionTextureStorage(importer);

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.LabelField("Channel", channelLabel);
            EditorGUILayout.ObjectField("Texture Asset", vatTexture, typeof(Texture2D), false);
            EditorGUILayout.LabelField("Dimensions", $"{vatTexture.width} x {vatTexture.height}");
            EditorGUILayout.LabelField("Auto Layout", layout.UsesPackedRows ? "Packed rows" : "One frame per row");
            EditorGUILayout.LabelField("Importer Limit", $"{layout.RequiredImporterSize} x {layout.RequiredImporterSize}");
            EditorGUILayout.LabelField("Stored Channels", storage == VATPositionTextureStorage.RGB24 ? "RGB (position only)" : "RGBA (alpha unused)");
            EditorGUI.EndDisabledGroup();

            bool unityImporterProtected = IsVATTextureImporterProtected(importer, layout, storage);
            string unityStatus = unityImporterProtected
                ? $"Unity importer: Linear, Point, Clamp, no mipmaps, {storage} and uncompressed for Default/WebGL."
                : "Unity importer protection is incomplete. Re-bake this VAT asset to restore the required importer settings.";
            EditorGUILayout.HelpBox(unityStatus, unityImporterProtected ? MessageType.Info : MessageType.Error);

            LunaTextureOverrideSettings lunaSettings;
            bool hasLunaOverride = TryGetCachedLunaTextureOverride(textureAssetPath, out lunaSettings);
            bool lunaProtected = hasLunaOverride && IsLunaTextureProtected(lunaSettings, layout, storage);

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

            EditorGUILayout.HelpBox(
                "VAT Position Texture is never resized or lossily compressed. RGB24 is safe only because this shader samples RGB position data and does not use alpha.",
                MessageType.None);
            EditorGUILayout.HelpBox(
                "This is baked-output information. Change bake settings in 1. Settings, then re-bake to apply them; Outputs never rewrites generated VAT assets.",
                MessageType.None);
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

            EditorGUILayout.HelpBox(
                "This panel reports the generated material Base Textures. Configure the profile in 1. Settings and re-bake to apply a different profile.",
                MessageType.None);

            bool allConfigured = true;
            for (int i = 0; i < baseTextures.Count; i++)
            {
                Texture2D texture = baseTextures[i];
                string textureAssetPath = AssetDatabase.GetAssetPath(texture);
                int requiredDimension = Mathf.Max(texture.width, texture.height);
                LunaTextureOverrideSettings lunaSettings;
                bool hasLunaOverride = TryGetCachedLunaTextureOverride(textureAssetPath, out lunaSettings);
                bool isConfigured = hasLunaOverride &&
                                    IsLunaTextureConfigured(lunaSettings, requiredDimension, requiredDimension, null);
                allConfigured &= isConfigured;

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField($"Base Texture [{i}]", texture, typeof(Texture2D), false);
                EditorGUILayout.LabelField(
                    "Luna Export",
                    hasLunaOverride
                        ? $"{lunaSettings.MaxWidth} x {lunaSettings.MaxHeight} | {lunaSettings.Format} | {lunaSettings.Compression} | Quality {lunaSettings.Quality}"
                        : "No per-texture override");
                EditorGUI.EndDisabledGroup();

                if (!isConfigured)
                {
                    EditorGUILayout.HelpBox(
                        $"'{texture.name}' has no valid Luna texture override for its baked dimensions.",
                        MessageType.Error);
                }
            }

            if (allConfigured)
            {
                EditorGUILayout.HelpBox(
                    "All VAT material base textures have a valid Luna override for their baked dimensions.",
                    MessageType.Info);
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

        private static bool TryCalculateVATTextureLayout(
            int vertexCount,
            int totalBakeFrames,
            out VATTextureLayout layout,
            out string error)
        {
            layout = new VATTextureLayout();
            error = null;
            if (vertexCount <= 0 || totalBakeFrames <= 0)
            {
                error = "VAT layout requires at least one vertex and one sampled frame.";
                return false;
            }

            long texelCount = (long)vertexCount * totalBakeFrames;
            long maximumTexelCount = (long)MaxVATTextureDimension * MaxVATTextureDimension;
            if (texelCount > maximumTexelCount)
            {
                error =
                    $"VAT requires {texelCount:N0} position texels, exceeding the {maximumTexelCount:N0} texel capacity of a " +
                    $"{MaxVATTextureDimension} x {MaxVATTextureDimension} texture. Reduce vertices, clips, or sample FPS.";
                return false;
            }

            if (vertexCount <= MaxVATTextureDimension && totalBakeFrames <= MaxVATTextureDimension)
            {
                layout = CreateVATTextureLayout(vertexCount, totalBakeFrames, false);
                return true;
            }

            int minimumWidth = (int)((texelCount + MaxVATTextureDimension - 1) / MaxVATTextureDimension);
            int nearSquareWidth = Mathf.CeilToInt(Mathf.Sqrt((float)texelCount));
            int packedWidth = Mathf.Clamp(
                Mathf.Max(minimumWidth, nearSquareWidth),
                1,
                MaxVATTextureDimension);
            int packedHeight = (int)((texelCount + packedWidth - 1) / packedWidth);
            if (packedHeight > MaxVATTextureDimension)
            {
                error =
                    $"VAT could not pack {texelCount:N0} position texels inside the supported " +
                    $"{MaxVATTextureDimension} x {MaxVATTextureDimension} texture.";
                return false;
            }

            layout = CreateVATTextureLayout(packedWidth, packedHeight, true);
            return true;
        }

        private static VATTextureLayout CreateVATTextureLayout(int width, int height, bool usesPackedRows)
        {
            int largestDimension = Mathf.Max(width, height);
            return new VATTextureLayout
            {
                Width = width,
                Height = height,
                RequiredImporterSize = largestDimension > 0
                    ? Mathf.NextPowerOfTwo(largestDimension)
                    : 0,
                UsesPackedRows = usesPackedRows
            };
        }

        private static bool IsVATTextureImporterProtected(
            TextureImporter importer,
            VATTextureLayout layout,
            VATPositionTextureStorage storage)
        {
            if (importer == null || layout.Width <= 0 || layout.Height <= 0 ||
                layout.Width > MaxVATTextureDimension || layout.Height > MaxVATTextureDimension ||
                layout.RequiredImporterSize > MaxVATTextureDimension)
            {
                return false;
            }

            TextureImporterPlatformSettings defaultSettings = importer.GetDefaultPlatformTextureSettings();
            TextureImporterPlatformSettings webglSettings = importer.GetPlatformTextureSettings("WebGL");
            bool defaultProtected = IsVATTexturePlatformProtected(defaultSettings, layout.RequiredImporterSize, storage);
            bool webglProtected = IsVATTexturePlatformProtected(webglSettings, layout.RequiredImporterSize, storage);

            return !importer.sRGBTexture &&
                   importer.textureCompression == TextureImporterCompression.Uncompressed &&
                   importer.filterMode == FilterMode.Point &&
                   importer.wrapMode == TextureWrapMode.Clamp &&
                   !importer.mipmapEnabled &&
                   !importer.isReadable &&
                   defaultProtected &&
                   webglProtected;
        }

        private static bool IsVATTexturePlatformProtected(
            TextureImporterPlatformSettings settings,
            int requiredDimension,
            VATPositionTextureStorage storage)
        {
            return settings.overridden &&
                   settings.format == GetVATTextureImporterFormat(storage) &&
                   settings.textureCompression == TextureImporterCompression.Uncompressed &&
                   !settings.crunchedCompression &&
                   settings.maxTextureSize >= requiredDimension;
        }

        private static TextureImporterFormat GetVATTextureImporterFormat(
            VATPositionTextureStorage storage)
        {
            return storage == VATPositionTextureStorage.RGB24
                ? TextureImporterFormat.RGB24
                : TextureImporterFormat.RGBA32;
        }

        private static VATPositionTextureStorage GetVATPositionTextureStorage(TextureImporter importer)
        {
            if (importer == null)
            {
                return VATPositionTextureStorage.RGBA32;
            }

            TextureImporterPlatformSettings defaultSettings = importer.GetDefaultPlatformTextureSettings();
            TextureImporterPlatformSettings webglSettings = importer.GetPlatformTextureSettings("WebGL");
            return defaultSettings.format == TextureImporterFormat.RGB24 &&
                   webglSettings.format == TextureImporterFormat.RGB24
                ? VATPositionTextureStorage.RGB24
                : VATPositionTextureStorage.RGBA32;
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
                _weaponOutputNames.Add("Item");
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
                   ? VATRendererRole.Item
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
            return string.IsNullOrWhiteSpace(weaponName) ? "Item" : weaponName.Trim();
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

            string expectedFolderName = GetAssetNameToken(assetDataName);
            // Once a VATAssetDataSO is already inside its output folder, reuse
            // it directly instead of creating <VATData>/<VATData> on re-bake.
            _activeOutputDirectory = string.Equals(
                Path.GetFileName(parentDirectory),
                expectedFolderName,
                StringComparison.OrdinalIgnoreCase)
                ? parentDirectory
                : Path.Combine(parentDirectory, expectedFolderName).Replace('\\', '/');
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

        /// <summary>
        /// Moves legacy output created beside its VATAssetDataSO-named folder
        /// into that folder. AssetDatabase.MoveAsset preserves GUIDs, so scene,
        /// prefab and asset references remain valid.
        /// </summary>
        private bool MoveExistingOutputAssetsIntoActiveDirectory(VATAssetDataSO assetData)
        {
            if (assetData == null || string.IsNullOrEmpty(_activeOutputDirectory))
            {
                return true;
            }

            List<UnityEngine.Object> assetsToMove = new List<UnityEngine.Object>();
            CollectOutputAsset(assetsToMove, assetData.BakedStaticMesh);
            CollectOutputAsset(assetsToMove, assetData.VATTexture);
            CollectOutputMaterials(assetsToMove, assetData.BakedMaterials);
            CollectOutputAsset(assetsToMove, assetData.AnimatorAsset);

            if (assetData.WeaponAssets != null)
            {
                for (int i = 0; i < assetData.WeaponAssets.Count; i++)
                {
                    VATWeaponAssetEntry weaponEntry = assetData.WeaponAssets[i];
                    CollectWeaponOutputAssets(
                        assetsToMove,
                        weaponEntry == null ? null : weaponEntry.WeaponAsset);
                }
            }
            CollectWeaponOutputAssets(assetsToMove, assetData.DefaultWeaponAsset);

            // Move the source asset last so failures leave it easy to find.
            CollectOutputAsset(assetsToMove, assetData);

            HashSet<string> processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int movedCount = 0;
            for (int i = 0; i < assetsToMove.Count; i++)
            {
                UnityEngine.Object asset = assetsToMove[i];
                if (asset == null) continue;

                string sourcePath = AssetDatabase.GetAssetPath(asset);
                if (string.IsNullOrEmpty(sourcePath))
                {
                    continue;
                }
                sourcePath = sourcePath.Replace('\\', '/');
                if (!processedPaths.Add(sourcePath)) continue;

                if (IsAssetInDirectory(sourcePath, _activeOutputDirectory))
                {
                    continue;
                }

                if (!sourcePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning(
                        $"[VATBakeTool] Kept output reference '{sourcePath}' outside Assets; " +
                        "it cannot be moved into the VAT output folder.");
                    continue;
                }

                string targetPath = Path.Combine(_activeOutputDirectory, Path.GetFileName(sourcePath))
                    .Replace('\\', '/');
                UnityEngine.Object conflict = AssetDatabase.LoadMainAssetAtPath(targetPath);
                if (conflict != null && conflict != asset)
                {
                    Debug.LogError(
                        $"[VATBakeTool] Cannot move '{sourcePath}' into '{_activeOutputDirectory}': " +
                        $"'{targetPath}' already belongs to another asset. Resolve the duplicate and bake again.");
                    return false;
                }

                string moveError = AssetDatabase.MoveAsset(sourcePath, targetPath);
                if (!string.IsNullOrEmpty(moveError))
                {
                    Debug.LogError(
                        $"[VATBakeTool] Failed to move existing output '{sourcePath}' into " +
                        $"'{_activeOutputDirectory}': {moveError}");
                    return false;
                }

                movedCount++;
            }

            if (movedCount > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log(
                    $"[VATBakeTool] Moved {movedCount} existing VAT output asset(s) into " +
                    $"'{_activeOutputDirectory}' before overriding them.");
            }

            return true;
        }

        private static bool IsAssetInDirectory(string assetPath, string directory)
        {
            string normalizedDirectory = directory.Replace('\\', '/').TrimEnd('/');
            string normalizedAssetPath = assetPath.Replace('\\', '/');
            return normalizedAssetPath.StartsWith(
                normalizedDirectory + "/",
                StringComparison.OrdinalIgnoreCase);
        }

        private static void CollectOutputAsset(
            List<UnityEngine.Object> assets,
            UnityEngine.Object asset)
        {
            if (asset != null)
            {
                assets.Add(asset);
            }
        }

        private static void CollectOutputMaterials(
            List<UnityEngine.Object> assets,
            List<Material> materials)
        {
            if (materials == null) return;
            for (int i = 0; i < materials.Count; i++)
            {
                CollectOutputAsset(assets, materials[i]);
            }
        }

        private static void CollectWeaponOutputAssets(
            List<UnityEngine.Object> assets,
            VATWeaponAssetSO weaponAsset)
        {
            if (weaponAsset == null) return;
            CollectOutputAsset(assets, weaponAsset.BakedStaticMesh);
            CollectOutputAsset(assets, weaponAsset.VATTexture);
            CollectOutputMaterials(assets, weaponAsset.BakedMaterials);
            CollectOutputAsset(assets, weaponAsset);
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

            // Build one Body channel and one string-named VAT channel for every Item group.
            List<SkinnedMeshRenderer> bodyRenderers = new List<SkinnedMeshRenderer>();
            Dictionary<string, WeaponBakeGroup> weaponGroupsByName =
                new Dictionary<string, WeaponBakeGroup>(StringComparer.OrdinalIgnoreCase);
            List<WeaponBakeGroup> weaponGroups = new List<WeaponBakeGroup>();
            for (int i = 0; i < _detectedSkinnedMeshes.Count; i++)
            {
                if (i < _selectedMeshToggles.Count && _selectedMeshToggles[i] && _detectedSkinnedMeshes[i] != null)
                {
                    VATRendererRole role = i < _meshRoles.Count ? _meshRoles[i] : VATRendererRole.Body;
                    if (role == VATRendererRole.Item)
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

            // Capture runtime scale metadata from the complete hierarchy. This
            // is intentionally separate from the bake coordinate transform:
            // Body and optional sub/Item channels are still baked at default
            // scale in normalized common-parent space.
            PrepareVATBakeCoordinateSpace(_detectedSkinnedMeshes);

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
            string outputName = GetConfiguredOutputName();
            VATAssetDataSO existingOutputAsset = GetExistingOutputAsset(outputName);
            bool overrideMode = ResolveOverrideMode(existingOutputAsset, out cancelled);
            if (cancelled) return;

            ConfigureActiveOutputDirectory(outputName, existingOutputAsset);
            if (overrideMode && !MoveExistingOutputAssetsIntoActiveDirectory(existingOutputAsset))
            {
                return;
            }

            // The output mode controls material validation independently for each
            // VAT channel. Body and every named Item group have separate
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
                        $"VAT Bake Stopped: Item Material Validation ({weaponGroup.Name})",
                        validationMessage,
                        "OK");
                    return;
                }
            }

            _activeLunaTextureSetupBatch = new LunaTextureSetupBatch();
            BeginNormalizedBakeHierarchy();

            try
            {
                BakedOutput output = BakeOutput(
                    bodyRenderers,
                    bodyMaterialSlots,
                    weaponGroups,
                    clipsToBake,
                    outputName,
                    existingOutputAsset,
                    overrideMode);

                if (output != null)
                {
                    CommitLunaTextureSetupBatch(_activeLunaTextureSetupBatch);
                    _outputAssetData = output.AssetData;
                }
            }
            finally
            {
                RestoreNormalizedBakeHierarchy();
                _activeLunaTextureSetupBatch = null;
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

            string vatShaderName = _enableOutline
                ? "OptimizedFeature/VAT_Unlit_Luna"
                : "OptimizedFeature/VAT_Unlit_Luna_NoOutline";
            Shader vatShader = Shader.Find(vatShaderName);
            if (vatShader == null)
            {
                Debug.LogError($"[VATBakeTool] Could not find shader '{vatShaderName}'.");
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
                    string weaponChannelName = outputName + "_Item_" + GetAssetNameToken(weaponGroup.Name);
                    weaponGroup.ExistingAsset = existingAsset != null
                        ? existingAsset.GetWeaponAssetByEditorName(weaponGroup.Name)
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
                _enableOutline,
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
                    string weaponChannelName = outputName + "_Item_" + GetAssetNameToken(weaponGroup.Name);
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
                        _enableOutline,
                        vatShader);
                    if (weapon.Materials.Count != weaponGroup.Materials.Count)
                    {
                        Debug.LogError(
                            $"[VATBakeTool] Failed to create all Item VAT materials for '{weaponGroup.Name}'.");
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
                        WeaponHash = VATClipInfo.GenerateHash(weaponGroup.Name),
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
            ForceDefaultVATBakeHierarchyScale();

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

            VATTextureLayout textureLayout;
            string layoutError;
            if (!TryCalculateVATTextureLayout(vertexCount, totalBakeFrames, out textureLayout, out layoutError))
            {
                DestroyImmediate(bakedMesh);
                Debug.LogError(
                    $"[VATBakeTool] Cannot bake '{channelName}': {layoutError}");
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

            Bounds staticBounds = bakedMesh.bounds;
            Mesh outputMesh = SaveMeshAsset(bakedMesh, channelName, existingMesh, overrideMode);
            Vector3 boundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 boundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            Vector3[] frameVertices = new Vector3[vertexCount];
            Vector3[] firstFrameVertices = new Vector3[vertexCount];
            bool hasFirstFrame = false;
            float maxFrameMotion = 0f;
            bool animatorWasEnabled = _detectedAnimator != null && _detectedAnimator.enabled;
            Texture2D vatTexture = null;
            bool vatTextureReadyForSave = false;
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
                int sampledGlobalFrame = 0;
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
                        ForceDefaultVATBakeHierarchyScale();
                        if (!BakeFrameVertices(sources, frameVertices) ||
                            !ValidateFrameVertices(
                                sources,
                                frameVertices,
                                staticBounds,
                                channelName,
                                clip,
                                frame,
                                time,
                                sampledGlobalFrame))
                        {
                            return null;
                        }

                        if (!hasFirstFrame)
                        {
                            Array.Copy(frameVertices, firstFrameVertices, vertexCount);
                            outputMesh.vertices = firstFrameVertices;
                            outputMesh.RecalculateBounds();
                            EditorUtility.SetDirty(outputMesh);
                            Debug.Log(
                                $"[VATBakeTool] Synchronized static preview mesh '{outputMesh.name}' " +
                                "with the first baked frame.");
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

                        sampledGlobalFrame++;
                    }
                }

                Vector3 padding = (boundsMax - boundsMin) * 0.03f;
                boundsMin -= padding;
                boundsMax += padding;
                Vector3 boundsSize = boundsMax - boundsMin;
                if (boundsSize.x <= 0f) boundsSize.x = 1f;
                if (boundsSize.y <= 0f) boundsSize.y = 1f;
                if (boundsSize.z <= 0f) boundsSize.z = 1f;

                TextureFormat bakeTextureFormat = _vatPositionTextureStorage == VATPositionTextureStorage.RGB24
                    ? TextureFormat.RGB24
                    : TextureFormat.RGBAHalf;
                vatTexture = new Texture2D(
                    textureLayout.Width,
                    textureLayout.Height,
                    bakeTextureFormat,
                    false);
                vatTexture.name = channelName + "_Texture";
                int encodedGlobalFrame = 0;
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
                        ForceDefaultVATBakeHierarchyScale();
                        if (!BakeFrameVertices(sources, frameVertices) ||
                            !ValidateFrameVertices(
                                sources,
                                frameVertices,
                                staticBounds,
                                channelName,
                                clip,
                                frame,
                                time,
                                encodedGlobalFrame))
                        {
                            return null;
                        }
                        for (int vertex = 0; vertex < vertexCount; vertex++)
                        {
                            Vector3 position = frameVertices[vertex];
                            int texelIndex = encodedGlobalFrame * vertexCount + vertex;
                            int texelX = texelIndex % textureLayout.Width;
                            int texelY = texelIndex / textureLayout.Width;
                            vatTexture.SetPixel(texelX, texelY, new Color(
                                Mathf.Clamp01((position.x - boundsMin.x) / boundsSize.x),
                                Mathf.Clamp01((position.y - boundsMin.y) / boundsSize.y),
                                Mathf.Clamp01((position.z - boundsMin.z) / boundsSize.z),
                                1f));
                        }
                        encodedGlobalFrame++;
                    }
                }

                vatTexture.Apply();
                vatTextureReadyForSave = true;
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

                    ReleasePoseMeshes(sources);

                    if (!vatTextureReadyForSave && vatTexture != null)
                    {
                        DestroyImmediate(vatTexture);
                        vatTexture = null;
                    }
                }
            }

            if (maxFrameMotion <= 0.00001f)
            {
                Debug.LogWarning(
                    $"[VATBakeTool] No vertex motion was detected while sampling '{channelName}'. " +
                    $"Animation root: '{animationSampleRoot.name}'. Check that the selected clips bind to this hierarchy.");
            }

            Texture2D outputTexture = SaveVATTextureAsset(
                vatTexture,
                channelName,
                existingTexture,
                textureLayout,
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
            VATTextureLayout layout,
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
            ConfigureVATTextureImporter(textureAssetPath, _vatPositionTextureStorage, layout);
            TextureImporter vatImporter = AssetImporter.GetAtPath(textureAssetPath) as TextureImporter;
            if (!IsVATTextureImporterProtected(
                    vatImporter,
                    layout,
                    _vatPositionTextureStorage))
            {
                Debug.LogError(
                    $"[VATBakeTool] Unity importer protection could not be verified for '{textureAssetPath}'. " +
                    "Bake stopped so this VAT texture cannot be exported with altered data.");
                return null;
            }
            if (!QueueLunaTextureSetup(
                    textureAssetPath,
                    layout.Width,
                    layout.Height,
                    GetVATTextureExportSettings(_vatPositionTextureStorage)))
            {
                Debug.LogError(
                    $"[VATBakeTool] VAT texture '{textureAssetPath}' was baked, but its Luna setup could not be queued. " +
                    "The generated asset remains available, but do not export it with Luna until a re-bake succeeds.");
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
                    RendererReferenceWorld = renderer.transform.localToWorldMatrix,
                    // A renderer with valid skinning data must resolve its own
                    // bindposes/boneWeights against the current bone matrices,
                    // including when its GameObject is placed below a bone. The
                    // frame path writes directly in target-root space.
                    // Only a mesh without skinning data is driven by its hierarchy.
                    UsesHierarchyTransform = !HasSkinningData(renderer, sharedMesh)
                };

                source.RigidBone = TryGetSingleRigidBone(renderer, sharedMesh);
                if (source.RigidBone != null)
                {
                    source.RigidBoneReferenceWorld = source.RigidBone.localToWorldMatrix;
                    source.RigidBoneIndex = FindBoneIndex(renderer.bones, source.RigidBone);
                    ConfigureRigidBakeMode(source);
                }

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

        private bool BakeFrameVertices(
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

                if (source.UsesHierarchyTransform)
                {
                    Vector3[] sourceVertices = source.SharedMesh.vertices;
                    int copyCount = Mathf.Min(source.VertexCount, sourceVertices.Length);
                    for (int vertexIndex = 0; vertexIndex < copyCount; vertexIndex++)
                    {
                        combinedVertices[source.VertexOffset + vertexIndex] =
                            source.RendererToTarget.MultiplyPoint3x4(sourceVertices[vertexIndex]);
                    }
                }
                else if (source.RigidBone != null && source.UseRigidReferencePoseFallback)
                {
                    BakeRigidSkinnedVerticesInTargetSpace(source, combinedVertices);
                }
                else if (source.RigidBone != null && source.UseManualRigidSkinning)
                {
                    BakeManualRigidSkinnedVerticesInTargetSpace(source, combinedVertices);
                }
                else if (!BakeSkinnedVerticesInTargetSpace(source, combinedVertices))
                {
                    return false;
                }
            }

            return true;
        }

        private void BakeRigidSkinnedVerticesInTargetSpace(
            MeshBakeSource source,
            Vector3[] combinedVertices)
        {
            // Rigid sub-SkinnedMeshes can have importer-specific bindposes that
            // do not describe the same local space as sharedMesh.vertices. Use
            // Unity's baked reference-pose vertices when available, preserve
            // their renderer transform (including authored scale), and apply
            // only the animated rigid-bone delta. This keeps the result in the
            // same VAT target-root space as the normal SkinnedMesh path.
            Matrix4x4 boneDelta =
                source.RigidBone.localToWorldMatrix * source.RigidBoneReferenceWorld.inverse;
            Matrix4x4 rigidToTarget =
                GetVATBakeRootWorldToLocalMatrix() *
                boneDelta *
                source.RendererReferenceWorld;

            Vector3[] sourceVertices = source.SharedMesh.vertices;
            IList<Vector3> referencePoseVertices = source.PoseVertices;
            bool hasReferencePose = referencePoseVertices != null &&
                                    referencePoseVertices.Count == source.VertexCount;

            for (int vertexIndex = 0; vertexIndex < source.VertexCount; vertexIndex++)
            {
                Vector3 vertex = hasReferencePose
                    ? referencePoseVertices[vertexIndex]
                    : sourceVertices[vertexIndex];
                combinedVertices[source.VertexOffset + vertexIndex] =
                    rigidToTarget.MultiplyPoint3x4(vertex);
            }
        }

        private void BakeManualRigidSkinnedVerticesInTargetSpace(
            MeshBakeSource source,
            Vector3[] combinedVertices)
        {
            // Keep this dispatch point for serialized/editor state compatibility.
            // The old bindpose-only reconstruction ignored the renderer's baked
            // reference geometry and was the source of scale/offset errors on
            // sub-SkinnedMeshes. The reference-pose path preserves that data.
            BakeRigidSkinnedVerticesInTargetSpace(source, combinedVertices);
        }

        private void ConfigureRigidBakeMode(MeshBakeSource source)
        {
            if (source == null || source.Renderer == null || source.SharedMesh == null ||
                source.RigidBone == null)
            {
                return;
            }

            if (source.PoseMesh == null)
            {
                source.PoseMesh = new Mesh
                {
                    name = source.Renderer.name + "_VATRigidValidation",
                    hideFlags = HideFlags.HideAndDontSave
                };
            }

            try
            {
                // BakeMesh is the authoritative Unity skinning result. Keep the
                // renderer scale disabled here because RendererToTarget applies
                // the current renderer transform exactly once below.
                source.Renderer.BakeMesh(source.PoseMesh, false);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[VATBakeTool] BakeMesh validation failed for rigid renderer " +
                    $"'{GetTransformPath(source.Renderer.transform)}': {exception.Message}. " +
                    "Using rigid reference-pose reconstruction when possible.");
                source.UseManualRigidSkinning = false;
                source.UseRigidReferencePoseFallback = true;
                return;
            }

            source.PoseVertices.Clear();
            source.PoseMesh.GetVertices(source.PoseVertices);
            if (source.PoseVertices.Count != source.VertexCount)
            {
                Debug.LogWarning(
                    $"[VATBakeTool] BakeMesh validation vertex count mismatch for " +
                    $"'{GetTransformPath(source.Renderer.transform)}'. Expected {source.VertexCount}, " +
                    $"got {source.PoseVertices.Count}. Using rigid reference-pose reconstruction.");
                source.UseManualRigidSkinning = false;
                source.UseRigidReferencePoseFallback = true;
                return;
            }

            Matrix4x4 expectedToTarget;
            if (!TryGetRigidSkinningMatrix(source, out expectedToTarget))
            {
                // The bindpose is not reliable for this imported rigid mesh,
                // but the Unity-baked reference vertices are still valid. The
                // reference-pose reconstruction only needs the rigid bone
                // delta and therefore remains usable.
                source.UseManualRigidSkinning = true;
                return;
            }

            float maxError = 0f;
            Vector3[] sourceVertices = source.SharedMesh.vertices;
            for (int vertexIndex = 0; vertexIndex < source.VertexCount; vertexIndex++)
            {
                Vector3 baked = source.RendererToTarget.MultiplyPoint3x4(
                    source.PoseVertices[vertexIndex]);
                Vector3 expected = expectedToTarget.MultiplyPoint3x4(sourceVertices[vertexIndex]);
                maxError = Mathf.Max(maxError, Vector3.Distance(baked, expected));
            }

            source.RigidBakeValidationError = maxError;
            Bounds expectedBounds = CalculateTransformedBounds(sourceVertices, expectedToTarget);
            float tolerance = Mathf.Max(0.001f, expectedBounds.size.magnitude * 0.001f);
            if (maxError <= tolerance)
            {
                return;
            }

            Debug.LogWarning(
                $"[VATBakeTool] Rigid BakeMesh validation mismatch for " +
                $"'{GetTransformPath(source.Renderer.transform)}': max error {maxError:F6}, " +
                $"tolerance {tolerance:F6}. Renderer scale={source.Renderer.transform.localScale}. " +
                "Using the Unity-baked rigid reference pose.");

            source.UseManualRigidSkinning = true;
            Debug.Log(
                $"[VATBakeTool] Using Unity-baked rigid reference-pose reconstruction for " +
                $"'{GetTransformPath(source.Renderer.transform)}'.");
        }

        private bool TryGetRigidSkinningMatrix(
            MeshBakeSource source,
            out Matrix4x4 rigidToTarget)
        {
            rigidToTarget = Matrix4x4.identity;
            if (source == null || source.Renderer == null || source.SharedMesh == null ||
                source.RigidBone == null || source.RigidBoneIndex < 0 ||
                source.RigidBoneIndex >= source.SharedMesh.bindposes.Length)
            {
                return false;
            }

            Transform[] bones = source.Renderer.bones;
            if (bones == null || source.RigidBoneIndex >= bones.Length ||
                bones[source.RigidBoneIndex] != source.RigidBone)
            {
                return false;
            }

            rigidToTarget = GetVATBakeRootWorldToLocalMatrix() *
                            source.RigidBone.localToWorldMatrix *
                            source.SharedMesh.bindposes[source.RigidBoneIndex];
            return true;
        }

        private static int FindBoneIndex(Transform[] bones, Transform targetBone)
        {
            if (bones == null || targetBone == null)
            {
                return -1;
            }

            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == targetBone)
                {
                    return i;
                }
            }

            return -1;
        }

        private static Bounds CalculateTransformedBounds(
            Vector3[] vertices,
            Matrix4x4 transform)
        {
            Bounds bounds = new Bounds();
            if (vertices == null || vertices.Length == 0)
            {
                return bounds;
            }

            Vector3 first = transform.MultiplyPoint3x4(vertices[0]);
            bounds = new Bounds(first, Vector3.zero);
            for (int i = 1; i < vertices.Length; i++)
            {
                bounds.Encapsulate(transform.MultiplyPoint3x4(vertices[i]));
            }

            return bounds;
        }

        private bool BakeSkinnedVerticesInTargetSpace(
            MeshBakeSource source,
            Vector3[] combinedVertices)
        {
            if (source.PoseMesh == null)
            {
                source.PoseMesh = new Mesh
                {
                    name = source.Renderer.name + "_VATPose",
                    hideFlags = HideFlags.HideAndDontSave
                };
            }

            try
            {
                // Let Unity resolve the renderer's bone, bindpose, root-bone and
                // renderer-space conventions. Reimplementing this matrix chain
                // manually is fragile for imported FBX hierarchies and renderers
                // mounted below another bone.
                // Scale is applied exactly once below through RendererToTarget.
                source.Renderer.BakeMesh(source.PoseMesh, false);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[VATBakeTool] Unity BakeMesh failed for '{GetTransformPath(source.Renderer.transform)}': " +
                    exception.Message);
                return false;
            }

            source.PoseVertices.Clear();
            source.PoseMesh.GetVertices(source.PoseVertices);
            if (source.PoseVertices.Count != source.VertexCount)
            {
                Debug.LogError(
                    $"[VATBakeTool] BakeMesh vertex count mismatch for '{GetTransformPath(source.Renderer.transform)}'. " +
                    $"Expected {source.VertexCount}, got {source.PoseVertices.Count}.");
                return false;
            }

            for (int vertexIndex = 0; vertexIndex < source.VertexCount; vertexIndex++)
            {
                // BakeMesh returns vertices in renderer-local space. Convert them
                // to the same target-root space used by the static mesh and VAT.
                combinedVertices[source.VertexOffset + vertexIndex] =
                    source.RendererToTarget.MultiplyPoint3x4(source.PoseVertices[vertexIndex]);
            }

            return true;
        }

        private static void ReleasePoseMeshes(List<MeshBakeSource> sources)
        {
            if (sources == null)
            {
                return;
            }

            for (int i = 0; i < sources.Count; i++)
            {
                Mesh poseMesh = sources[i].PoseMesh;
                if (poseMesh != null)
                {
                    DestroyImmediate(poseMesh);
                    sources[i].PoseMesh = null;
                }
                sources[i].PoseVertices.Clear();
            }
        }

        private bool ValidateFrameVertices(
            List<MeshBakeSource> sources,
            Vector3[] frameVertices,
            Bounds staticBounds,
            string channelName,
            AnimationClip clip,
            int frameIndex,
            float time,
            int globalFrameIndex)
        {
            float staticMagnitude = Mathf.Max(
                1f,
                Mathf.Max(
                    Mathf.Abs(staticBounds.min.x),
                    Mathf.Max(
                        Mathf.Abs(staticBounds.min.y),
                        Mathf.Abs(staticBounds.min.z))));
            float allowedMagnitude = Mathf.Max(
                VATFrameValidationMinimumMagnitude,
                staticMagnitude * VATFrameValidationMagnitudeMultiplier);

            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                MeshBakeSource source = sources[sourceIndex];
                float sourceMaxMagnitude = 0f;
                bool hasInvalidValue = false;
                int firstInvalidVertex = -1;

                for (int vertexIndex = 0; vertexIndex < source.VertexCount; vertexIndex++)
                {
                    Vector3 position = frameVertices[source.VertexOffset + vertexIndex];
                    bool invalid = float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z) ||
                                   float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z);
                    float magnitude = Mathf.Max(
                        Mathf.Abs(position.x),
                        Mathf.Max(Mathf.Abs(position.y), Mathf.Abs(position.z)));
                    sourceMaxMagnitude = Mathf.Max(sourceMaxMagnitude, magnitude);

                    if (invalid || magnitude > allowedMagnitude)
                    {
                        hasInvalidValue = true;
                        if (firstInvalidVertex < 0)
                        {
                            firstInvalidVertex = vertexIndex;
                        }
                    }
                }

                if (hasInvalidValue)
                {
                    Debug.LogError(
                        $"[VATBakeTool] Invalid VAT frame rejected. Channel='{channelName}', " +
                        $"Clip='{clip.name}', localFrame={frameIndex}, globalFrame={globalFrameIndex}, " +
                        $"time={time:F4}, Renderer='{GetTransformPath(source.Renderer.transform)}', " +
                        $"vertex={firstInvalidVertex}, maxAbsPosition={sourceMaxMagnitude:F4}, " +
                        $"allowedAbsPosition={allowedMagnitude:F4}. " +
                        "The source FBX pose or renderer mapping must be fixed before exporting VAT.");
                    return false;
                }
            }

            return true;
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
            {
                return "null";
            }

            List<string> path = new List<string>();
            Transform current = transform;
            while (current != null)
            {
                path.Add(current.name);
                current = current.parent;
            }
            path.Reverse();
            return string.Join("/", path.ToArray());
        }

        private static Transform TryGetSingleRigidBone(
            SkinnedMeshRenderer renderer,
            Mesh mesh)
        {
            if (renderer == null || mesh == null || mesh.boneWeights == null ||
                mesh.boneWeights.Length != mesh.vertexCount || renderer.bones == null)
            {
                return null;
            }

            BoneWeight[] weights = mesh.boneWeights;
            Transform[] bones = renderer.bones;
            int rigidBoneIndex = -1;
            for (int vertexIndex = 0; vertexIndex < weights.Length; vertexIndex++)
            {
                BoneWeight weight = weights[vertexIndex];
                int vertexBoneIndex = -1;
                float totalWeight = 0f;
                if (!TryAccumulateRigidWeight(weight.boneIndex0, weight.weight0, bones.Length, ref vertexBoneIndex, ref totalWeight) ||
                    !TryAccumulateRigidWeight(weight.boneIndex1, weight.weight1, bones.Length, ref vertexBoneIndex, ref totalWeight) ||
                    !TryAccumulateRigidWeight(weight.boneIndex2, weight.weight2, bones.Length, ref vertexBoneIndex, ref totalWeight) ||
                    !TryAccumulateRigidWeight(weight.boneIndex3, weight.weight3, bones.Length, ref vertexBoneIndex, ref totalWeight) ||
                    vertexBoneIndex < 0 || totalWeight < 0.99f)
                {
                    return null;
                }

                if (rigidBoneIndex < 0)
                {
                    rigidBoneIndex = vertexBoneIndex;
                }
                else if (rigidBoneIndex != vertexBoneIndex)
                {
                    return null;
                }
            }

            return rigidBoneIndex >= 0 && rigidBoneIndex < bones.Length
                ? bones[rigidBoneIndex]
                : null;
        }

        private static bool TryAccumulateRigidWeight(
            int boneIndex,
            float weight,
            int boneCount,
            ref int vertexBoneIndex,
            ref float totalWeight)
        {
            if (weight <= 0.0001f)
            {
                return true;
            }

            if (boneIndex < 0 || boneIndex >= boneCount)
            {
                return false;
            }

            if (vertexBoneIndex < 0)
            {
                vertexBoneIndex = boneIndex;
            }
            else if (vertexBoneIndex != boneIndex)
            {
                return false;
            }

            totalWeight += weight;
            return true;
        }

        private Matrix4x4 GetVATBakeRootWorldToLocalMatrix()
        {
            Transform bakeRoot = _vatBakeRoot != null
                ? _vatBakeRoot
                : _targetPrefab.transform;
            // This matrix converts source vertices to the common parent's
            // local space. The world-to-local/local-to-world multiplication
            // removes every ancestor transform above that parent. Do not use
            // the captured runtime ModelScale here: it is metadata only.
            return bakeRoot.worldToLocalMatrix;
        }

        private void BeginNormalizedBakeHierarchy()
        {
            _normalizedBakeScaleTransforms.Clear();
            _originalBakeScaleValues.Clear();

            if (!NormalizeModelScaleDuringBake || _vatBakeRoot == null)
            {
                return;
            }

            Transform current = _vatBakeRoot;
            while (current != null)
            {
                _normalizedBakeScaleTransforms.Add(current);
                _originalBakeScaleValues.Add(current.localScale);
                current.localScale = Vector3.one;
                current = current.parent;
            }

            Debug.Log(
                $"[VATBakeTool] Temporarily normalized " +
                $"{_normalizedBakeScaleTransforms.Count} hierarchy scale(s) from " +
                $"'{GetTransformPath(_vatBakeRoot)}' to " +
                $"'{GetTransformPath(_vatBakeRoot.root)}'.");
        }

        private void ForceDefaultVATBakeHierarchyScale()
        {
            if (_normalizedBakeScaleTransforms.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _normalizedBakeScaleTransforms.Count; i++)
            {
                Transform transform = _normalizedBakeScaleTransforms[i];
                if (transform != null)
                {
                    transform.localScale = Vector3.one;
                }
            }
        }

        private void RestoreNormalizedBakeHierarchy()
        {
            for (int i = 0; i < _normalizedBakeScaleTransforms.Count; i++)
            {
                Transform transform = _normalizedBakeScaleTransforms[i];
                if (transform != null && i < _originalBakeScaleValues.Count)
                {
                    transform.localScale = _originalBakeScaleValues[i];
                }
            }

            _normalizedBakeScaleTransforms.Clear();
            _originalBakeScaleValues.Clear();
        }

        private Matrix4x4 GetRendererToTargetMatrix(SkinnedMeshRenderer renderer)
        {
            // Convert every source to the first common SkinnedMesh parent local
            // space. Its local scale is intentionally cancelled here; the scale
            // is stored in VATAssetDataSO and restored by Runtime Setup.
            return GetVATBakeRootWorldToLocalMatrix() * renderer.transform.localToWorldMatrix;
        }

        private void PrepareVATBakeCoordinateSpace(IList<SkinnedMeshRenderer> renderers)
        {
            _vatBakeRoot = FindFirstCommonSkinnedMeshParent(renderers);
            if (_vatBakeRoot == null)
            {
                _vatBakeRoot = _targetPrefab != null
                    ? _targetPrefab.transform
                    : null;
            }

            // Capture the authored hierarchy scale for Runtime Setup only.
            // BakeOutput normalizes the complete parent chain and derives
            // vertex positions from the resulting default-scale hierarchy.
            _capturedModelScale = CalculateCumulativeHierarchyScale(_vatBakeRoot);

            if (_vatBakeRoot != null)
            {
                Debug.Log(
                    $"[VATBakeTool] VAT coordinate root='{GetTransformPath(_vatBakeRoot)}', " +
                    $"captured cumulative model scale={_capturedModelScale} " +
                    $"through hierarchy root '{GetTransformPath(_vatBakeRoot.root)}'. " +
                    "Bake space uses normalized scale (1,1,1).");
            }
        }

        private static Vector3 CalculateCumulativeHierarchyScale(Transform commonParent)
        {
            if (commonParent == null)
            {
                return Vector3.one;
            }

            Vector3 cumulativeScale = Vector3.one;
            Transform current = commonParent;
            while (current != null)
            {
                cumulativeScale = Vector3.Scale(cumulativeScale, current.localScale);
                current = current.parent;
            }

            return IsUsableModelScale(cumulativeScale)
                ? cumulativeScale
                : Vector3.one;
        }

        private Transform FindFirstCommonSkinnedMeshParent(IList<SkinnedMeshRenderer> renderers)
        {
            if (_targetPrefab == null || renderers == null || renderers.Count == 0)
            {
                return _targetPrefab != null ? _targetPrefab.transform : null;
            }

            Transform common = null;
            int validRendererCount = 0;
            for (int i = 0; i < renderers.Count; i++)
            {
                SkinnedMeshRenderer renderer = renderers[i];
                if (renderer == null) continue;

                validRendererCount++;

                common = common == null
                    ? renderer.transform
                    : FindCommonTransformAncestor(common, renderer.transform);
                if (common == null) break;
            }

            if (common == null || !IsTransformWithin(common, _targetPrefab.transform))
            {
                return _targetPrefab.transform;
            }

            // With only one renderer, the renderer itself is not the model
            // parent requested by the bake contract.
            if (validRendererCount == 1 &&
                common.GetComponent<SkinnedMeshRenderer>() != null &&
                common != _targetPrefab.transform && common.parent != null)
            {
                common = common.parent;
            }

            return common;
        }

        private static Transform FindCommonTransformAncestor(Transform first, Transform second)
        {
            HashSet<Transform> ancestors = new HashSet<Transform>();
            Transform current = first;
            while (current != null)
            {
                ancestors.Add(current);
                current = current.parent;
            }

            current = second;
            while (current != null)
            {
                if (ancestors.Contains(current)) return current;
                current = current.parent;
            }

            return null;
        }

        private static bool IsTransformWithin(Transform candidate, Transform root)
        {
            Transform current = candidate;
            while (current != null)
            {
                if (current == root) return true;
                current = current.parent;
            }

            return false;
        }

        private static bool IsUsableModelScale(Vector3 scale)
        {
            return !float.IsNaN(scale.x) && !float.IsInfinity(scale.x) &&
                   !float.IsNaN(scale.y) && !float.IsInfinity(scale.y) &&
                   !float.IsNaN(scale.z) && !float.IsInfinity(scale.z) &&
                   Mathf.Abs(scale.x) > 0.000001f &&
                   Mathf.Abs(scale.y) > 0.000001f &&
                   Mathf.Abs(scale.z) > 0.000001f;
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

        private VATAssetDataSO GetExistingOutputAsset(string outputName)
        {
            if (_outputAssetData == null || string.IsNullOrWhiteSpace(outputName))
            {
                return null;
            }

            string expectedAssetDataName = outputName + "Data";
            if (string.Equals(
                    _outputAssetData.name,
                    expectedAssetDataName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return _outputAssetData;
            }

            // Unity normally syncs ScriptableObject.name with its file name, but
            // checking the path also keeps legacy assets eligible for override.
            string assetPath = AssetDatabase.GetAssetPath(_outputAssetData);
            string fileName = string.IsNullOrEmpty(assetPath)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(assetPath);
            return string.Equals(
                    fileName,
                    expectedAssetDataName,
                    StringComparison.OrdinalIgnoreCase)
                ? _outputAssetData
                : null;
        }

        private bool ResolveOverrideMode(
            VATAssetDataSO existingOutputAsset,
            out bool cancelled)
        {
            cancelled = false;
            if (existingOutputAsset == null) return false;

            int dialogChoice = EditorUtility.DisplayDialogComplex(
                "VAT Asset Data Already Exists",
                $"The output currently contains '{existingOutputAsset.name}'.\n\n" +
                "• Override — Move legacy output into its VATAssetDataSO-named folder, then overwrite it (keeps GUIDs and references).\n" +
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

        private static void ConfigureVATTextureImporter(
            string textureAssetPath,
            VATPositionTextureStorage storage,
            VATTextureLayout layout)
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
            defaultSettings.format = GetVATTextureImporterFormat(storage);
            defaultSettings.textureCompression = TextureImporterCompression.Uncompressed;
            defaultSettings.maxTextureSize = layout.RequiredImporterSize;
            importer.SetPlatformTextureSettings(defaultSettings);

            TextureImporterPlatformSettings webglSettings = importer.GetPlatformTextureSettings("WebGL");
            webglSettings.overridden = true;
            webglSettings.format = GetVATTextureImporterFormat(storage);
            webglSettings.textureCompression = TextureImporterCompression.Uncompressed;
            webglSettings.maxTextureSize = layout.RequiredImporterSize;
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
            bool enableOutline,
            Shader vatShader)
        {
            List<Material> outputMaterials = new List<Material>();
            int vatTextureId = Shader.PropertyToID("_VATTex");
            int boundsMinId = Shader.PropertyToID("_BoundingMin");
            int boundsMaxId = Shader.PropertyToID("_BoundingMax");
            int framesId = Shader.PropertyToID("_NumFrames");
            int verticesId = Shader.PropertyToID("_NumVertices");
            int textureWidthId = Shader.PropertyToID("_VATTextureWidth");
            int textureHeightId = Shader.PropertyToID("_VATTextureHeight");

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
                    CopyBaseTextureAndTint(originalMaterial, outputMaterial);
                    outputMaterial.SetTexture(vatTextureId, vatTexture);
                    outputMaterial.SetVector(boundsMinId, boundsMin);
                    outputMaterial.SetVector(boundsMaxId, boundsMax);
                    outputMaterial.SetFloat(framesId, totalBakeFrames);
                    outputMaterial.SetFloat(verticesId, vertexCount);
                    outputMaterial.SetFloat(textureWidthId, vatTexture.width);
                    outputMaterial.SetFloat(textureHeightId, vatTexture.height);
                    ApplyBakedOutlineSettings(outputMaterial, enableOutline);
                    outputMaterial.enableInstancing = true;
                    EditorUtility.SetDirty(outputMaterial);
                }
                else
                {
                    outputMaterial = Instantiate(originalMaterial);
                    string materialSuffix = materialSlots.Count > 1 ? "_" + i : string.Empty;
                    outputMaterial.name = originalMaterial.name + "_VAT" + materialSuffix;
                    outputMaterial.shader = vatShader;
                    CopyBaseTextureAndTint(originalMaterial, outputMaterial);
                    outputMaterial.SetTexture(vatTextureId, vatTexture);
                    outputMaterial.SetVector(boundsMinId, boundsMin);
                    outputMaterial.SetVector(boundsMaxId, boundsMax);
                    outputMaterial.SetFloat(framesId, totalBakeFrames);
                    outputMaterial.SetFloat(verticesId, vertexCount);
                    outputMaterial.SetFloat(textureWidthId, vatTexture.width);
                    outputMaterial.SetFloat(textureHeightId, vatTexture.height);
                    ApplyBakedOutlineSettings(outputMaterial, enableOutline);
                    outputMaterial.enableInstancing = true;

                    string materialAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                        Path.Combine(GetActiveOutputDirectory(), outputMaterial.name + ".mat").Replace('\\', '/'));
                    AssetDatabase.CreateAsset(outputMaterial, materialAssetPath);
                    outputMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialAssetPath);
                }

                outputMaterials.Add(outputMaterial);
            }

            return outputMaterials;
        }

        private static void ApplyBakedOutlineSettings(Material material, bool enabled)
        {
            if (material == null) return;

            if (material.HasProperty("_Outline"))
            {
                material.SetFloat("_Outline", enabled ? 1f : 0f);
            }

            if (material.HasProperty("_OutlineWidthIndependent"))
            {
                if (!enabled)
                {
                    material.SetFloat("_OutlineWidthIndependent", 0f);
                }

                bool widthIndependent = enabled &&
                                        material.GetFloat("_OutlineWidthIndependent") > 0.5f;
                if (widthIndependent)
                {
                    material.EnableKeyword("OUTLINE_WIDTH_INDEPENDENT");
                }
                else
                {
                    material.DisableKeyword("OUTLINE_WIDTH_INDEPENDENT");
                }
            }

            if (enabled)
            {
                material.EnableKeyword("OUTLINE");
            }
            else
            {
                material.DisableKeyword("OUTLINE");
            }
        }

        private bool EnsureMaterialBaseTexturesLunaProtected(List<Material> materials)
        {
            List<Texture2D> baseTextures = CollectMaterialBaseTextures(materials);
            LunaTextureExportSettings exportSettings = GetVisualTextureExportSettings(_baseTextureLunaFormat);
            for (int i = 0; i < baseTextures.Count; i++)
            {
                Texture2D texture = baseTextures[i];
                string textureAssetPath = AssetDatabase.GetAssetPath(texture);
                int requiredDimension = Mathf.Max(texture.width, texture.height);
                if (!QueueLunaTextureSetup(
                        textureAssetPath,
                        requiredDimension,
                        requiredDimension,
                        exportSettings))
                {
                    Debug.LogError(
                        $"[VATBakeTool] Base texture '{texture.name}' could not be queued for Luna setup. " +
                        "The bake will continue, but do not export it with Luna until a re-bake succeeds.");
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
                assetData.ModelScale = _capturedModelScale;
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
                assetData.ModelScale = _capturedModelScale;

                string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.Combine(GetActiveOutputDirectory(), outputName + "Data.asset").Replace('\\', '/'));
                AssetDatabase.CreateAsset(assetData, assetPath);
            }

            if (assetData.Clips == null) assetData.Clips = new List<VATClipInfo>();
            SetClipManifest(assetData.Clips, clipsToBake, clipFramesList, _sampleFrameRate);
            assetData.DefaultStateName = assetData.Clips.Count > 0 && assetData.Clips[0] != null
                ? assetData.Clips[0].StateHash
                : 0;
            if (assetData.WeaponAssets == null) assetData.WeaponAssets = new List<VATWeaponAssetEntry>();
            assetData.WeaponAssets.Clear();
            if (weaponEntries != null)
            {
                assetData.WeaponAssets.AddRange(weaponEntries);
            }
            assetData.DefaultWeaponAsset = assetData.WeaponAssets.Count > 0
                ? assetData.WeaponAssets[0].WeaponAsset
                : null;

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

            string outputName = GetConfiguredOutputName();
            string outputDirectory = Path.Combine(_savePath, outputName + "Data");
            if (!Directory.Exists(outputDirectory)) return false;

            string[] outputFiles = Directory.GetFiles(
                outputDirectory,
                outputName + "*",
                SearchOption.TopDirectoryOnly);
            return outputFiles.Length > 0;
        }

        private bool QueueLunaTextureSetup(
            string assetPath,
            int requiredWidth,
            int requiredHeight,
            LunaTextureExportSettings exportSettings)
        {
            if (_activeLunaTextureSetupBatch == null)
            {
                return false;
            }

            return _activeLunaTextureSetupBatch.Queue(
                assetPath,
                requiredWidth,
                requiredHeight,
                exportSettings);
        }

        private static bool CommitLunaTextureSetupBatch(LunaTextureSetupBatch batch)
        {
            if (batch == null || batch.Count == 0)
            {
                return true;
            }

            string lunaJsonPath = GetLunaJsonPath();
            if (!File.Exists(lunaJsonPath))
            {
                Debug.LogError(
                    $"[VATBakeTool] Luna texture setup is pending for {batch.Count} texture asset(s): luna.json was not found. " +
                    "The bake output remains available, but no Luna include/override was saved. Restore luna.json and re-bake.");
                return false;
            }

            try
            {
                string jsonText = File.ReadAllText(lunaJsonPath);
                bool changed = false;
                foreach (LunaTextureSetupRequest request in batch.Requests)
                {
                    if (!TryEnsureLunaAssetInclude(ref jsonText, request.AssetPath, ref changed) ||
                        !TryEnsureLunaTextureOverride(
                            ref jsonText,
                            request.AssetPath,
                            request.RequiredWidth,
                            request.RequiredHeight,
                            request.ExportSettings,
                            ref changed))
                    {
                        Debug.LogError(
                            $"[VATBakeTool] Luna texture setup is pending for {batch.Count} texture asset(s): " +
                            $"the luna.json schema could not add '{request.AssetPath}'. " +
                            "No batched Luna changes were saved. Repair the schema and re-bake.");
                        return false;
                    }
                }

                // This is deliberately the only luna.json write in a bake run.
                if (changed)
                {
                    File.WriteAllText(lunaJsonPath, jsonText);
                }

                int unverifiedCount = 0;
                foreach (LunaTextureSetupRequest request in batch.Requests)
                {
                    LunaTextureOverrideSettings lunaSettings;
                    bool isProtected = TryGetLunaTextureOverride(request.AssetPath, out lunaSettings) &&
                                       IsLunaTextureConfigured(
                                           lunaSettings,
                                           request.RequiredWidth,
                                           request.RequiredHeight,
                                           request.ExportSettings);
                    if (!isProtected)
                    {
                        unverifiedCount++;
                    }
                }

                if (unverifiedCount > 0)
                {
                    Debug.LogError(
                        $"[VATBakeTool] Luna texture setup could not be verified for {unverifiedCount}/{batch.Count} texture asset(s). " +
                        "The bake output remains available; re-bake after resolving luna.json access.");
                    return false;
                }

                Debug.Log(
                    changed
                        ? $"[VATBakeTool] Applied {batch.Count} Luna texture setup(s) with one luna.json write."
                        : $"[VATBakeTool] Verified {batch.Count} Luna texture setup(s); luna.json already matched.");
                return true;
            }
            catch (IOException ex)
            {
                bool isUserMappedFile = ex.Message.IndexOf("1224", StringComparison.Ordinal) >= 0;
                string lockReason = isUserMappedFile
                    ? "luna.json is locked by another process using a memory-mapped file (Windows error 1224)."
                    : ex.Message;
                Debug.LogError(
                    $"[VATBakeTool] Luna texture setup is pending for {batch.Count} texture asset(s). {lockReason} " +
                    "The bake output remains available, but no batched Luna include/override was saved. " +
                    "Close the active Luna build/preview and re-bake to retry.");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[VATBakeTool] Luna texture setup is pending for {batch.Count} texture asset(s): {ex.Message} " +
                    "The bake output remains available. Re-bake after resolving luna.json access.");
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
            int requiredWidth,
            int requiredHeight,
            LunaTextureExportSettings exportSettings,
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
                    requiredWidth,
                    requiredHeight,
                    exportSettings,
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
            string overrideEntry = BuildLunaTextureOverride(
                assetPath,
                requiredWidth,
                requiredHeight,
                exportSettings,
                objectIndent,
                newLine);
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
                LunaJsonDocument root = JsonUtility.FromJson<LunaJsonDocument>(
                    File.ReadAllText(lunaJsonPath));
                LunaJsonTextureOverride[] overrides = root != null && root.assets != null &&
                    root.assets.rules != null && root.assets.rules.texture != null
                    ? root.assets.rules.texture.overrides
                    : null;
                if (overrides == null) return false;

                string normalizedPath = assetPath.Replace('\\', '/');
                for (int i = 0; i < overrides.Length; i++)
                {
                    LunaJsonTextureOverride candidate = overrides[i];
                    if (candidate == null ||
                        !string.Equals(candidate.name, normalizedPath, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    settings.Exists = true;
                    settings.MaxWidth = candidate.maxWidth;
                    settings.MaxHeight = candidate.maxHeight;
                    settings.Format = candidate.format ?? string.Empty;
                    settings.Compression = candidate.compression ?? string.Empty;
                    settings.Quality = candidate.quality;
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

        private static bool IsLunaTextureProtected(
            LunaTextureOverrideSettings settings,
            VATTextureLayout layout,
            VATPositionTextureStorage storage)
        {
            return IsLunaTextureConfigured(
                settings,
                layout.Width,
                layout.Height,
                GetVATTextureExportSettings(storage));
        }

        private static bool IsLunaTextureConfigured(
            LunaTextureOverrideSettings settings,
            int requiredWidth,
            int requiredHeight,
            LunaTextureExportSettings? expectedSettings)
        {
            bool hasExportSettings = !string.IsNullOrEmpty(settings.Format) &&
                                     !string.IsNullOrEmpty(settings.Compression) &&
                                     settings.Quality > 0;
            return settings.Exists &&
                   settings.MaxWidth >= requiredWidth &&
                   settings.MaxHeight >= requiredHeight &&
                   hasExportSettings &&
                   (!expectedSettings.HasValue ||
                    (string.Equals(settings.Format, expectedSettings.Value.Format, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(settings.Compression, expectedSettings.Value.Compression, StringComparison.OrdinalIgnoreCase) &&
                     settings.Quality >= expectedSettings.Value.Quality));
        }

        private static LunaTextureExportSettings GetVATTextureExportSettings(
            VATPositionTextureStorage storage)
        {
            return new LunaTextureExportSettings
            {
                Format = storage == VATPositionTextureStorage.RGB24 ? "png24" : LunaVATTextureFormat,
                Compression = LunaVATTextureCompression,
                Quality = LunaVATTextureQuality
            };
        }

        private static LunaTextureExportSettings GetVisualTextureExportSettings(
            LunaVisualTextureFormat format)
        {
            switch (format)
            {
                case LunaVisualTextureFormat.PNG24:
                    return new LunaTextureExportSettings
                    {
                        Format = "png24",
                        Compression = "none",
                        Quality = 100
                    };
                case LunaVisualTextureFormat.PNG8:
                    return new LunaTextureExportSettings
                    {
                        Format = "png8",
                        Compression = "none",
                        Quality = 100
                    };
                case LunaVisualTextureFormat.JPEG:
                    return new LunaTextureExportSettings
                    {
                        Format = "jpeg",
                        Compression = "none",
                        Quality = 70
                    };
                case LunaVisualTextureFormat.Webp:
                    return new LunaTextureExportSettings
                    {
                        Format = "webp",
                        Compression = "none",
                        Quality = 90
                    };
                default:
                    return GetVATTextureExportSettings(VATPositionTextureStorage.RGBA32);
            }
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
            int requiredWidth,
            int requiredHeight,
            LunaTextureExportSettings exportSettings,
            string objectIndent,
            string newLine)
        {
            string propertyIndent = objectIndent + "    ";
            return "{" + newLine +
                   propertyIndent + "\"maxWidth\": " + requiredWidth + "," + newLine +
                   propertyIndent + "\"maxHeight\": " + requiredHeight + "," + newLine +
                   propertyIndent + "\"format\": \"" + exportSettings.Format + "\"," + newLine +
                   propertyIndent + "\"compression\": \"" + exportSettings.Compression + "\"," + newLine +
                   propertyIndent + "\"quality\": " + exportSettings.Quality + "," + newLine +
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
