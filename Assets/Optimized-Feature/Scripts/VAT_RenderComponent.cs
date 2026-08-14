using System.Collections.Generic;
using UnityEngine;

namespace OptimizedFeature.Scripts
{
    /// <summary>
    /// Unified VAT component replacing Unity's Animator + SkinnedMeshRenderer.
    /// Merges animation state machine, mesh/material binding, and shader property management
    /// into a single component to reduce MonoBehaviour overhead (critical for Luna/WebGL).
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class VAT_RenderComponent : MonoBehaviour
    {
        // --- Mesh & Material References ---
        [SerializeField] private MeshFilter _meshFilter;
        [SerializeField] private MeshRenderer _meshRenderer;
        [SerializeField] private VATAssetDataSO _vatAssetData;

        // --- Animator Settings ---
        [SerializeField] private string _defaultStateName = "Idle";
        [SerializeField] private float _speed = 1.0f;

        // --- Pooled State Data (avoid GC allocations on Play/CrossFade) ---
        private VATAnimStateData _stateA;
        private VATAnimStateData _stateB;
        private VATAnimStateData _currentState;
        private VATAnimStateData _targetState;
        private float _currentStateTime;
        private float _targetStateTime;
        private float _transitionDuration;
        private float _transitionTimer;
        private bool _isBlending;

        // --- Shader Property Cache ---
        private MaterialPropertyBlock _propertyBlock;
        private int _vatTexId;
        private int _boundingMinId;
        private int _boundingMaxId;
        private int _numFramesId;
        private int _numVerticesId;
        private int _frameIndexLowerId;
        private int _frameIndexUpperId;
        private int _blendWeightId;

        // --- Visibility & Attachments ---
        private List<VAT_ObjectMesh> _attachedObjectMeshes = new List<VAT_ObjectMesh>();
        private bool _isVisible = true;
        private bool _registeredWithVATSystem;
        private readonly List<Renderer> _childRenderers = new List<Renderer>();

        // --- Public API ---
        public VATAssetDataSO VatAssetData => _vatAssetData;
        public MeshRenderer Renderer => _meshRenderer;
        public float Speed { get => _speed; set => _speed = value; }
        public string CurrentStateName => _currentState != null ? _currentState.StateName : string.Empty;
        public int CurrentStateHash => _currentState != null ? _currentState.StateHash : 0;
        public bool IsBlending => _isBlending;
        public bool IsVisible { get => _isVisible; set => _isVisible = value; }

        private void Awake()
        {
            if (_meshFilter == null) _meshFilter = GetComponent<MeshFilter>();
            if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();

            InitializeShaderPropertyIds();
            ApplyVATAssetData();

            // Pre-allocate 2 state instances to avoid GC allocations during Play/CrossFade
            _stateA = new VATAnimStateData(string.Empty, 0, 0, 0);
            _stateB = new VATAnimStateData(string.Empty, 0, 0, 0);

            GetComponentsInChildren(true, _attachedObjectMeshes);

            // Cache child Renderers except our own MeshRenderer
            Renderer[] allRenderers = GetComponentsInChildren<Renderer>(true);
            if (allRenderers != null)
            {
                int rLength = allRenderers.Length;
                for (int i = 0; i < rLength; i++)
                {
                    Renderer r = allRenderers[i];
                    if (r != null && r != _meshRenderer)
                    {
                        _childRenderers.Add(r);
                    }
                }
            }
        }

        private void Start()
        {
            // OnEnable can run before VATSystem.Awake when both objects are
            // loaded from a scene. Retry registration after all Awake calls so
            // the component cannot remain permanently stuck on frame 0.
            TryRegisterWithVATSystem();

            if (_vatAssetData == null)
            {
                Debug.LogWarning($"[VAT_RenderComponent] '{gameObject.name}' has no VATAssetData assigned! Animation will not play. " +
                    "Please assign VATAssetDataSO in Inspector or use Tools > VAT Setup Tester Helper.", gameObject);
                return;
            }

            if (!string.IsNullOrEmpty(_defaultStateName))
            {
                Play(_defaultStateName);
            }
        }

        private void OnEnable()
        {
            TryRegisterWithVATSystem();
        }

        private void OnDisable()
        {
            if (VATSystem.Instance != null)
            {
                VATSystem.Instance.UnregisterAnimator(this);
            }
            _registeredWithVATSystem = false;
        }

        private void TryRegisterWithVATSystem()
        {
            VATSystem system = VATSystem.Instance;
            if (system == null)
            {
                system = FindObjectOfType<VATSystem>();
            }

            if (system == null)
            {
                _registeredWithVATSystem = false;
                return;
            }

            system.RegisterAnimator(this);
            _registeredWithVATSystem = true;
        }

        private void Update()
        {
            // VATSystem is the normal driver. Keep a local fallback for
            // isolated prefab tests or scenes that do not contain a manager.
            if (!_registeredWithVATSystem && VATSystem.Instance != null)
            {
                TryRegisterWithVATSystem();
            }
            if (_registeredWithVATSystem) return;
            if (_vatAssetData == null || _currentState == null) return;

            ManualUpdate(Time.deltaTime, true);
        }

        // ========================================
        // Animator API
        // ========================================

        public void Play(string stateName)
        {
            if (_vatAssetData == null) return;

            int stateHash = VATClipInfo.GenerateHash(stateName);
            VATClipInfo clip = _vatAssetData.GetClip(stateHash);
            if (clip == null)
            {
                Debug.LogWarning($"[VAT_RenderComponent] Clip '{stateName}' (hash: {stateHash}) not found in VATAssetData!");
                return;
            }

            PlayInternal(clip);
        }

        public void Play(int stateHash)
        {
            if (_vatAssetData == null) return;

            VATClipInfo clip = _vatAssetData.GetClip(stateHash);
            if (clip == null)
            {
                Debug.LogWarning($"[VAT_RenderComponent] Clip hash '{stateHash}' not found in VATAssetData!");
                return;
            }

            PlayInternal(clip);
        }

        private void PlayInternal(VATClipInfo clip)
        {
            // Reuse pooled state — swap to whichever is NOT currently _currentState
            VATAnimStateData pooledState = (_currentState == _stateA) ? _stateB : _stateA;
            pooledState.Configure(clip.ClipName, clip.StateHash, clip.StartFrame, clip.EndFrame, clip.FrameRate, clip.IsLooping);
            _currentState = pooledState;
            _currentStateTime = 0f;
            _targetState = null;
            _isBlending = false;
            _transitionTimer = 0f;
            UpdateShaderFrames(clip.StartFrame, clip.StartFrame, 0f);
        }

        public void CrossFade(string stateName, float transitionDuration = 0.15f)
        {
            if (_vatAssetData == null) return;

            int stateHash = VATClipInfo.GenerateHash(stateName);
            if (_currentState != null && _currentState.StateHash == stateHash) return;

            VATClipInfo clip = _vatAssetData.GetClip(stateHash);
            if (clip == null)
            {
                Debug.LogWarning($"[VAT_RenderComponent] Clip '{stateName}' (hash: {stateHash}) not found in VATAssetData!");
                return;
            }

            CrossFadeInternal(clip, transitionDuration);
        }

        public void CrossFade(int stateHash, float transitionDuration = 0.15f)
        {
            if (_vatAssetData == null) return;
            if (_currentState != null && _currentState.StateHash == stateHash) return;

            VATClipInfo clip = _vatAssetData.GetClip(stateHash);
            if (clip == null)
            {
                Debug.LogWarning($"[VAT_RenderComponent] Clip hash '{stateHash}' not found in VATAssetData!");
                return;
            }

            CrossFadeInternal(clip, transitionDuration);
        }

        private void CrossFadeInternal(VATClipInfo clip, float transitionDuration)
        {
            // Reuse pooled state — swap to whichever is NOT currently _currentState
            VATAnimStateData pooledState = (_currentState == _stateA) ? _stateB : _stateA;
            pooledState.Configure(clip.ClipName, clip.StateHash, clip.StartFrame, clip.EndFrame, clip.FrameRate, clip.IsLooping);
            _targetState = pooledState;
            _targetStateTime = 0f;
            _transitionDuration = Mathf.Max(0.01f, transitionDuration);
            _transitionTimer = 0f;
            _isBlending = true;
        }

        // ========================================
        // Mesh & Material API (absorbed from VAT_SkinnedMeshComponent)
        // ========================================

        public void SetVATAssetData(VATAssetDataSO assetData)
        {
            _vatAssetData = assetData;
            ApplyVATAssetData();
        }

        public void SetMaterial(Material material)
        {
            if (_meshRenderer != null)
            {
                _meshRenderer.sharedMaterial = material;
                ApplyVATAssetData();
            }
        }

        public void SetMaterials(Material[] materials)
        {
            if (_meshRenderer != null)
            {
                _meshRenderer.sharedMaterials = materials;
                ApplyVATAssetData();
            }
        }

        // ========================================
        // Visibility
        // ========================================

        public void SetVisibility(bool visible)
        {
            if (_isVisible == visible) return;
            _isVisible = visible;

            if (_meshRenderer != null)
            {
                _meshRenderer.enabled = visible;
            }

            int count = _childRenderers.Count;
            for (int i = 0; i < count; i++)
            {
                if (_childRenderers[i] != null)
                {
                    _childRenderers[i].enabled = visible;
                }
            }
        }

        public Bounds GetWorldBounds()
        {
            if (_vatAssetData == null)
            {
                return new Bounds(transform.position, Vector3.one);
            }

            Vector3 min = _vatAssetData.BoundingMin;
            Vector3 max = _vatAssetData.BoundingMax;
            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;

            Vector3 worldCenter = transform.TransformPoint(center);
            Vector3 scale = transform.lossyScale;
            Vector3 worldSize = new Vector3(
                size.x * Mathf.Abs(scale.x),
                size.y * Mathf.Abs(scale.y),
                size.z * Mathf.Abs(scale.z)
            );

            // Safety padding to avoid clipping/popping during deformation animations
            worldSize *= 1.15f;

            return new Bounds(worldCenter, worldSize);
        }

        // ========================================
        // Per-Frame Update (called by VATSystem)
        // ========================================

        public void ManualUpdate(float deltaTime, bool updateRenderer = true)
        {
            if (_currentState == null) return;

            float scaledDelta = deltaTime * _speed;
            _currentStateTime += scaledDelta;

            int frameLower = _currentState.CalculateFrameIndex(_currentStateTime);
            int frameUpper = frameLower;
            float blendWeight = 0f;

            if (_isBlending && _targetState != null)
            {
                _targetStateTime += scaledDelta;
                _transitionTimer += scaledDelta;

                frameUpper = _targetState.CalculateFrameIndex(_targetStateTime);
                blendWeight = Mathf.Clamp01(_transitionTimer / _transitionDuration);

                if (_transitionTimer >= _transitionDuration)
                {
                    _currentState = _targetState;
                    _currentStateTime = _targetStateTime;
                    _targetState = null;
                    _isBlending = false;
                    blendWeight = 0f;
                }
            }

            if (updateRenderer)
            {
                UpdateShaderFrames(frameLower, frameUpper, blendWeight);

                // Synchronize attached equipment socket transforms
                int activeFrame = _isBlending ? frameUpper : frameLower;
                int count = _attachedObjectMeshes.Count;
                for (int i = 0; i < count; i++)
                {
                    if (_attachedObjectMeshes[i] != null)
                    {
                        _attachedObjectMeshes[i].SynchronizeFrame(activeFrame);
                    }
                }
            }
        }

        // ========================================
        // Internal — Shader Property Management
        // ========================================

        private void InitializeShaderPropertyIds()
        {
            _propertyBlock = new MaterialPropertyBlock();
            _vatTexId = Shader.PropertyToID("_VATTex");
            _boundingMinId = Shader.PropertyToID("_BoundingMin");
            _boundingMaxId = Shader.PropertyToID("_BoundingMax");
            _numFramesId = Shader.PropertyToID("_NumFrames");
            _numVerticesId = Shader.PropertyToID("_NumVertices");
            _frameIndexLowerId = Shader.PropertyToID("_FrameIndexLower");
            _frameIndexUpperId = Shader.PropertyToID("_FrameIndexUpper");
            _blendWeightId = Shader.PropertyToID("_BlendWeight");
        }

        private void ApplyVATAssetData()
        {
            if (_propertyBlock == null) InitializeShaderPropertyIds();
            if (_vatAssetData == null) return;

            if (_meshFilter != null && _vatAssetData.BakedStaticMesh != null)
            {
                _meshFilter.sharedMesh = _vatAssetData.BakedStaticMesh;
            }

            if (_meshRenderer != null && _vatAssetData.BakedMaterials != null &&
                _vatAssetData.BakedMaterials.Count > 0)
            {
                _meshRenderer.sharedMaterials = _vatAssetData.BakedMaterials.ToArray();
            }

            _meshRenderer.GetPropertyBlock(_propertyBlock);
            if (_vatAssetData.VATTexture != null)
            {
                _propertyBlock.SetTexture(_vatTexId, _vatAssetData.VATTexture);
            }
            _propertyBlock.SetVector(_boundingMinId, _vatAssetData.BoundingMin);
            _propertyBlock.SetVector(_boundingMaxId, _vatAssetData.BoundingMax);
            _propertyBlock.SetFloat(_numFramesId, _vatAssetData.TotalFrames);
            _propertyBlock.SetFloat(_numVerticesId, _vatAssetData.TotalVertices);
            _meshRenderer.SetPropertyBlock(_propertyBlock);
        }

        private void UpdateShaderFrames(int frameLower, int frameUpper, float blendWeight)
        {
            if (_meshRenderer == null) return;
            _meshRenderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetFloat(_frameIndexLowerId, frameLower);
            _propertyBlock.SetFloat(_frameIndexUpperId, frameUpper);
            _propertyBlock.SetFloat(_blendWeightId, blendWeight);
            _meshRenderer.SetPropertyBlock(_propertyBlock);
        }
    }
}
