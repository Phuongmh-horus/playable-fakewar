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
        private sealed class RuntimeParameterValue
        {
            public int Id;
            public int NameHash;
            public VATAnimatorParameterType Type;
            public bool BoolValue;
            public bool TriggerValue;
            public float FloatValue;
            public Vector2 Vector2Value;
        }

        // --- Mesh & Material References ---
        [SerializeField] private MeshFilter _meshFilter;
        [SerializeField] private MeshRenderer _meshRenderer;
        [SerializeField] private VATAssetDataSO _vatAssetData;

        // --- Animator Settings ---
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
        private int _currentBlendTreeId = -1;
        private int _targetBlendTreeId = -1;
        private readonly List<RuntimeParameterValue> _parameterValues =
            new List<RuntimeParameterValue>();

        // --- Shader Property Cache ---
        // A MeshRenderer has one MaterialPropertyBlock per material slot. Luna's
        // generic SetPropertyBlock overload only updates the first slot for a
        // multi-material mesh, leaving every sub-renderer at its material default
        // frame. Keep a block for each baked material and write it by index.
        private MaterialPropertyBlock[] _propertyBlocks;
        private int _frameIndexLowerId;
        private int _frameIndexUpperId;
        private int _blendWeightId;

        // --- Visibility ---
        private bool _isVisible = true;
        private readonly List<Renderer> _childRenderers = new List<Renderer>();
        private readonly List<VATWeaponRenderComponent> _weaponRenderComponents =
            new List<VATWeaponRenderComponent>();

        private int _currentFrameLower;
        private int _currentFrameUpper;
        private float _currentBlendWeight;

        // --- Public API ---
        public VATAssetDataSO VatAssetData => _vatAssetData;
        public MeshRenderer Renderer => _meshRenderer;
        public float Speed { get => _speed; set => _speed = value; }
        public string CurrentStateName => _currentState != null ? _currentState.StateName : string.Empty;
        public int CurrentStateHash => _currentState != null ? _currentState.StateHash : 0;
        public bool IsBlending => _isBlending;
        public int DefaultStateHash => _vatAssetData == null ? 0 : _vatAssetData.EffectiveDefaultStateName;
        public int CurrentBlendTreeId => _currentBlendTreeId;
        public float CurrentStateNormalizedTime => GetNormalizedTime(_currentState, _currentStateTime);
        public bool IsVisible { get => _isVisible; set => SetVisibility(value); }
        public int CurrentFrameLower => _currentFrameLower;
        public int CurrentFrameUpper => _currentFrameUpper;
        public float CurrentBlendWeight => _currentBlendWeight;

        private void Awake()
        {
            if (_meshFilter == null) _meshFilter = GetComponent<MeshFilter>();
            if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();

            InitializeShaderPropertyIds();
            ApplyVATAssetData();

            // Pre-allocate 2 state instances to avoid GC allocations during Play/CrossFade
            _stateA = new VATAnimStateData(string.Empty, 0, 0, 0);
            _stateB = new VATAnimStateData(string.Empty, 0, 0, 0);
            RebuildAnimatorRuntimeData();

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

            RefreshWeaponSubRenders();
        }

        private void Start()
        {
            if (_vatAssetData == null)
            {
                Debug.LogWarning($"[VAT_RenderComponent] '{gameObject.name}' has no VATAssetData assigned! Animation will not play. " +
                    "Please assign VATAssetDataSO in Inspector or use VAT Bake Tool > 4. Runtime Setup.", gameObject);
                return;
            }

            PlayDefaultState();
        }

        private void OnEnable()
        {
            VATSystem.RegisterAnimator(this);
        }

        private void OnDisable()
        {
            VATSystem.UnregisterAnimator(this);
        }

        // ========================================
        // Animator API
        // ========================================

        public void PlayDefaultState()
        {
            if (_vatAssetData == null || _vatAssetData.EffectiveDefaultStateName == 0) return;
            Play(_vatAssetData.EffectiveDefaultStateName);
        }

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
            _currentBlendTreeId = -1;
            _targetBlendTreeId = -1;
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

        public void PlayBlendTree(int blendTreeId)
        {
            if (_vatAssetData == null) return;
            VATAnimatorBlendTreeData tree = FindBlendTree(blendTreeId);
            VATClipInfo clip = ResolveBlendTreeClip(tree);
            if (clip == null)
            {
                Debug.LogWarning($"[VAT_RenderComponent] BlendTree '{blendTreeId}' has no resolvable child clip.", gameObject);
                return;
            }

            PlayInternal(clip);
            _currentBlendTreeId = blendTreeId;
        }

        public void CrossFadeBlendTree(int blendTreeId, float transitionDuration = 0.15f)
        {
            if (_vatAssetData == null) return;
            VATAnimatorBlendTreeData tree = FindBlendTree(blendTreeId);
            VATClipInfo clip = ResolveBlendTreeClip(tree);
            if (clip == null)
            {
                Debug.LogWarning($"[VAT_RenderComponent] BlendTree '{blendTreeId}' has no resolvable child clip.", gameObject);
                return;
            }

            if (_currentState != null && _currentState.StateHash == clip.StateHash &&
                _currentBlendTreeId == blendTreeId) return;
            CrossFadeInternal(clip, transitionDuration, blendTreeId);
        }

        public void Stop()
        {
            _currentState = null;
            _targetState = null;
            _currentStateTime = 0f;
            _targetStateTime = 0f;
            _transitionTimer = 0f;
            _isBlending = false;
            _currentBlendTreeId = -1;
            _targetBlendTreeId = -1;
        }

        // ========================================
        // Animator Parameter API
        // ========================================

        public bool HasParameter(string parameterName)
        {
            return FindParameterValue(parameterName) != null;
        }

        public bool HasParameter(int parameterHash)
        {
            return FindParameterValue(parameterHash) != null;
        }

        public void SetTrigger(string parameterName)
        {
            SetTrigger(VATClipInfo.GenerateHash(parameterName));
        }

        public void SetTrigger(int parameterHash)
        {
            RuntimeParameterValue parameter = FindParameterValue(parameterHash);
            if (parameter != null && parameter.Type == VATAnimatorParameterType.Trigger)
            {
                parameter.TriggerValue = true;
            }
        }

        public void ResetTrigger(string parameterName)
        {
            ResetTrigger(VATClipInfo.GenerateHash(parameterName));
        }

        public void ResetTrigger(int parameterHash)
        {
            RuntimeParameterValue parameter = FindParameterValue(parameterHash);
            if (parameter != null && parameter.Type == VATAnimatorParameterType.Trigger)
            {
                parameter.TriggerValue = false;
            }
        }

        public bool IsTriggerSet(string parameterName)
        {
            return IsTriggerSet(VATClipInfo.GenerateHash(parameterName));
        }

        public bool IsTriggerSet(int parameterHash)
        {
            RuntimeParameterValue parameter = FindParameterValue(parameterHash);
            return parameter != null && parameter.Type == VATAnimatorParameterType.Trigger &&
                   parameter.TriggerValue;
        }

        public void SetBool(string parameterName, bool value)
        {
            SetBool(VATClipInfo.GenerateHash(parameterName), value);
        }

        public void SetBool(int parameterHash, bool value)
        {
            RuntimeParameterValue parameter = FindParameterValue(parameterHash);
            if (parameter != null && parameter.Type == VATAnimatorParameterType.Bool)
            {
                parameter.BoolValue = value;
            }
        }

        public bool GetBool(string parameterName)
        {
            return GetBool(VATClipInfo.GenerateHash(parameterName));
        }

        public bool GetBool(int parameterHash)
        {
            RuntimeParameterValue parameter = FindParameterValue(parameterHash);
            return parameter != null && parameter.Type == VATAnimatorParameterType.Bool &&
                   parameter.BoolValue;
        }

        public void SetFloat(string parameterName, float value)
        {
            SetFloat(VATClipInfo.GenerateHash(parameterName), value);
        }

        public void SetFloat(int parameterHash, float value)
        {
            RuntimeParameterValue parameter = FindParameterValue(parameterHash);
            if (parameter != null && parameter.Type == VATAnimatorParameterType.Float)
            {
                parameter.FloatValue = value;
            }
        }

        public float GetFloat(string parameterName)
        {
            return GetFloat(VATClipInfo.GenerateHash(parameterName));
        }

        public float GetFloat(int parameterHash)
        {
            RuntimeParameterValue parameter = FindParameterValue(parameterHash);
            return parameter != null && parameter.Type == VATAnimatorParameterType.Float
                ? parameter.FloatValue
                : 0f;
        }

        public void SetVector2(string parameterName, Vector2 value)
        {
            SetVector2(VATClipInfo.GenerateHash(parameterName), value);
        }

        public void SetVector2(int parameterHash, Vector2 value)
        {
            RuntimeParameterValue parameter = FindParameterValue(parameterHash);
            if (parameter != null && parameter.Type == VATAnimatorParameterType.Vector2)
            {
                parameter.Vector2Value = value;
            }
        }

        public Vector2 GetVector2(string parameterName)
        {
            return GetVector2(VATClipInfo.GenerateHash(parameterName));
        }

        public Vector2 GetVector2(int parameterHash)
        {
            RuntimeParameterValue parameter = FindParameterValue(parameterHash);
            return parameter != null && parameter.Type == VATAnimatorParameterType.Vector2
                ? parameter.Vector2Value
                : Vector2.zero;
        }

        public void ResetAllTriggers()
        {
            for (int i = 0; i < _parameterValues.Count; i++)
            {
                if (_parameterValues[i].Type == VATAnimatorParameterType.Trigger)
                {
                    _parameterValues[i].TriggerValue = false;
                }
            }
        }

        private void CrossFadeInternal(VATClipInfo clip, float transitionDuration)
        {
            CrossFadeInternal(clip, transitionDuration, -1);
        }

        private void CrossFadeInternal(VATClipInfo clip, float transitionDuration, int targetBlendTreeId)
        {
            if (clip == null) return;
            if (_currentState == null)
            {
                PlayInternal(clip);
                _currentBlendTreeId = targetBlendTreeId;
                return;
            }

            // Reuse pooled state — swap to whichever is NOT currently _currentState
            VATAnimStateData pooledState = (_currentState == _stateA) ? _stateB : _stateA;
            pooledState.Configure(clip.ClipName, clip.StateHash, clip.StartFrame, clip.EndFrame, clip.FrameRate, clip.IsLooping);
            _targetState = pooledState;
            _targetStateTime = 0f;
            _transitionDuration = Mathf.Max(0.01f, transitionDuration);
            _transitionTimer = 0f;
            _targetBlendTreeId = targetBlendTreeId;
            _isBlending = true;
        }

        // ========================================
        // Mesh & Material API (absorbed from VAT_SkinnedMeshComponent)
        // ========================================

        public void SetVATAssetData(VATAssetDataSO assetData)
        {
            _vatAssetData = assetData;
            Stop();
            RebuildAnimatorRuntimeData();
            ApplyVATAssetData();
            RefreshWeaponSubRenders();
        }

        public void RefreshAnimatorData()
        {
            RebuildAnimatorRuntimeData();
        }

        public void RefreshWeaponSubRenders(bool assignMissingWeaponAssets = true)
        {
            _weaponRenderComponents.Clear();
            VATWeaponRenderComponent[] weaponComponents =
                GetComponentsInChildren<VATWeaponRenderComponent>(true);
            if (weaponComponents != null)
            {
                for (int i = 0; i < weaponComponents.Length; i++)
                {
                    if (weaponComponents[i] != null && !_weaponRenderComponents.Contains(weaponComponents[i]))
                    {
                        _weaponRenderComponents.Add(weaponComponents[i]);
                    }
                }
            }

            if (!assignMissingWeaponAssets || _vatAssetData == null)
            {
                return;
            }

            for (int i = 0; i < _weaponRenderComponents.Count; i++)
            {
                VATWeaponRenderComponent weapon = _weaponRenderComponents[i];
                if (weapon != null && weapon.WeaponAsset == null)
                {
                    VATWeaponAssetSO weaponAsset = _vatAssetData.GetWeaponAsset(weapon.WeaponHash);
                    if (weaponAsset == null)
                    {
                        continue;
                    }

                    weapon.SetFrameSource(this);
                    weapon.SetWeaponAsset(weaponAsset);
                }
            }
        }

        public bool SetWeaponAsset(VATWeaponAssetSO weaponAsset)
        {
            int weaponHash = 0;
            return SetWeaponAssetInternal(weaponHash, weaponAsset);
        }

        /// <summary>
        /// Equips a baked item channel by its stable runtime hash.
        /// The legacy method/property names remain for API compatibility;
        /// item display names are Editor-only metadata and are not involved.
        /// </summary>
        public bool EquipWeapon(int weaponHash)
        {
            if (_vatAssetData == null)
            {
                Debug.LogWarning(
                    $"[VAT_RenderComponent] Cannot equip item hash {weaponHash}: VATAssetDataSO is missing.",
                    gameObject);
                return false;
            }

            VATWeaponAssetSO weaponAsset = _vatAssetData.GetWeaponAsset(weaponHash);
            if (weaponAsset == null)
            {
                Debug.LogWarning(
                    $"[VAT_RenderComponent] No baked item is registered for hash {weaponHash}.",
                    gameObject);
                return false;
            }

            return SetWeaponAssetInternal(weaponHash, weaponAsset);
        }

        // Kept as a concise hash-based setter for callers that previously used
        // SetWeaponAsset directly.
        public bool SetWeaponAsset(int weaponHash)
        {
            return EquipWeapon(weaponHash);
        }

        private bool SetWeaponAssetInternal(int weaponHash, VATWeaponAssetSO weaponAsset)
        {
            if (weaponAsset != null && !IsWeaponFrameManifestCompatible(weaponAsset))
            {
                string bodyName = _vatAssetData == null ? "<none>" : _vatAssetData.name;
                int bodyFrames = _vatAssetData == null ? 0 : _vatAssetData.TotalFrames;
                Debug.LogError(
                    $"[VAT_RenderComponent] Item VAT '{weaponAsset.name}' has " +
                    $"{weaponAsset.TotalFrames} frames but Body VAT '{bodyName}' has " +
                    $"{bodyFrames}, or its clip ranges differ. Item switching was rejected because the frame manifests differ.",
                    gameObject);
                return false;
            }

            RefreshWeaponSubRenders(false);
            if (_weaponRenderComponents.Count == 0)
            {
                return false;
            }

            VATWeaponRenderComponent weaponRenderer = _weaponRenderComponents[0];
            weaponRenderer.SetFrameSource(this);
            weaponRenderer.SetWeaponHash(weaponHash);
            weaponRenderer.SetWeaponAsset(weaponAsset);
            weaponRenderer.ApplyFrame(_currentFrameLower, _currentFrameUpper, _currentBlendWeight);
            return true;
        }

        public bool IsWeaponFrameManifestCompatible(VATWeaponAssetSO weaponAsset)
        {
            if (weaponAsset == null || _vatAssetData == null ||
                weaponAsset.TotalFrames != _vatAssetData.TotalFrames ||
                weaponAsset.Clips == null || _vatAssetData.Clips == null ||
                weaponAsset.Clips.Count != _vatAssetData.Clips.Count)
            {
                return false;
            }

            for (int i = 0; i < _vatAssetData.Clips.Count; i++)
            {
                VATClipInfo bodyClip = _vatAssetData.Clips[i];
                VATClipInfo weaponClip = weaponAsset.Clips[i];
                if (bodyClip == null || weaponClip == null ||
                    bodyClip.StateHash != weaponClip.StateHash ||
                    bodyClip.StartFrame != weaponClip.StartFrame ||
                    bodyClip.EndFrame != weaponClip.EndFrame ||
                    !Mathf.Approximately(bodyClip.FrameRate, weaponClip.FrameRate))
                {
                    return false;
                }
            }

            return true;
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

            for (int i = 0; i < _weaponRenderComponents.Count; i++)
            {
                if (_weaponRenderComponents[i] != null)
                {
                    _weaponRenderComponents[i].SetVisibility(visible);
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

            Bounds bounds = new Bounds(worldCenter, worldSize);
            for (int i = 0; i < _weaponRenderComponents.Count; i++)
            {
                VATWeaponRenderComponent weapon = _weaponRenderComponents[i];
                if (weapon != null && weapon.WeaponAsset != null)
                {
                    bounds.Encapsulate(weapon.GetWorldBounds());
                }
            }

            return bounds;
        }

        internal void CollectRuntimeBatchSources(List<VATRuntimeMeshBatcher.Source> sources)
        {
            if (sources == null) return;

            if (_meshFilter != null && _meshRenderer != null && _vatAssetData != null)
            {
                Material[] materials = _meshRenderer.sharedMaterials;
                if (materials != null && materials.Length == 1 && materials[0] != null)
                {
                    sources.Add(new VATRuntimeMeshBatcher.Source
                    {
                        MeshFilter = _meshFilter,
                        Renderer = _meshRenderer,
                        Mesh = _meshFilter.sharedMesh,
                        Material = materials[0],
                        Owner = this,
                        Weapon = null,
                        BoundsMin = _vatAssetData.BoundingMin,
                        BoundsMax = _vatAssetData.BoundingMax,
                        FrameLower = _currentFrameLower,
                        FrameUpper = _currentFrameUpper,
                        BlendWeight = _currentBlendWeight
                    });
                }
            }

            for (int i = 0; i < _weaponRenderComponents.Count; i++)
            {
                VATWeaponRenderComponent weapon = _weaponRenderComponents[i];
                if (weapon != null)
                {
                    weapon.AppendRuntimeBatchSource(sources, this);
                }
            }
        }

        // ========================================
        // Per-Frame Update (called by VATSystem)
        // ========================================

        public void ManualUpdate(float deltaTime, bool updateRenderer = true)
        {
            if (_currentState == null) return;

            if (!_isBlending)
            {
                TryEvaluateTransitions();
                if (!_isBlending) UpdateCurrentBlendTreeSelection();
            }

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
                    _currentBlendTreeId = _targetBlendTreeId;
                    _targetBlendTreeId = -1;
                    frameLower = frameUpper;
                    blendWeight = 0f;
                }
            }

            if (updateRenderer)
            {
                UpdateShaderFrames(frameLower, frameUpper, blendWeight);
            }
        }

        private void RebuildAnimatorRuntimeData()
        {
            _parameterValues.Clear();
            if (_vatAssetData == null || _vatAssetData.AnimatorParameters == null) return;

            for (int i = 0; i < _vatAssetData.AnimatorParameters.Count; i++)
            {
                VATAnimatorParameterData parameter = _vatAssetData.AnimatorParameters[i];
                if (parameter == null) continue;

                RuntimeParameterValue value = new RuntimeParameterValue
                {
                    Id = parameter.id,
                    NameHash = VATClipInfo.GenerateHash(parameter.parameterName),
                    Type = parameter.type,
                    BoolValue = parameter.defaultBool,
                    FloatValue = parameter.defaultFloat,
                    Vector2Value = parameter.defaultVector2,
                    TriggerValue = false
                };
                _parameterValues.Add(value);
            }
        }

        private RuntimeParameterValue FindParameterValue(string parameterName)
        {
            return FindParameterValue(VATClipInfo.GenerateHash(parameterName));
        }

        private RuntimeParameterValue FindParameterValue(int parameterHash)
        {
            for (int i = 0; i < _parameterValues.Count; i++)
            {
                RuntimeParameterValue value = _parameterValues[i];
                if (value.Id == parameterHash || value.NameHash == parameterHash) return value;
            }
            return null;
        }

        private RuntimeParameterValue FindParameterValueById(int parameterId)
        {
            for (int i = 0; i < _parameterValues.Count; i++)
            {
                if (_parameterValues[i].Id == parameterId) return _parameterValues[i];
            }
            return null;
        }

        private VATAnimatorParameterData FindParameterData(int parameterId)
        {
            if (_vatAssetData == null || _vatAssetData.AnimatorParameters == null) return null;
            for (int i = 0; i < _vatAssetData.AnimatorParameters.Count; i++)
            {
                VATAnimatorParameterData parameter = _vatAssetData.AnimatorParameters[i];
                if (parameter != null && parameter.id == parameterId) return parameter;
            }
            return null;
        }

        private VATAnimatorBlendTreeData FindBlendTree(int blendTreeId)
        {
            if (_vatAssetData == null || _vatAssetData.AnimatorBlendTrees == null) return null;
            for (int i = 0; i < _vatAssetData.AnimatorBlendTrees.Count; i++)
            {
                VATAnimatorBlendTreeData tree = _vatAssetData.AnimatorBlendTrees[i];
                if (tree != null && tree.id == blendTreeId) return tree;
            }
            return null;
        }

        private void TryEvaluateTransitions()
        {
            if (_vatAssetData == null || _vatAssetData.AnimatorTransitions == null ||
                _currentState == null) return;

            int currentStateHash = _currentState.StateHash;
            for (int i = 0; i < _vatAssetData.AnimatorTransitions.Count; i++)
            {
                VATAnimatorTransitionData transition = _vatAssetData.AnimatorTransitions[i];
                if (transition == null || transition.fromStateHash != currentStateHash ||
                    !IsTransitionReady(transition)) continue;

                VATClipInfo targetClip;
                int targetBlendTreeId;
                if (!TryResolveTransitionTarget(transition, out targetClip, out targetBlendTreeId)) continue;

                CrossFadeInternal(targetClip, transition.duration, targetBlendTreeId);
                ConsumeTransitionTriggers(transition);
                return;
            }
        }

        private bool IsTransitionReady(VATAnimatorTransitionData transition)
        {
            if (transition == null) return false;

            bool hasConditions = transition.conditions != null && transition.conditions.Count > 0;
            if (hasConditions)
            {
                for (int i = 0; i < transition.conditions.Count; i++)
                {
                    VATAnimatorConditionData condition = transition.conditions[i];
                    if (condition == null || !EvaluateCondition(condition)) return false;
                }
            }

            if (transition.hasExitTime && !HasReachedExitTime(transition.exitTime))
            {
                return false;
            }

            return transition.autoTransition || transition.hasExitTime || hasConditions;
        }

        private bool HasReachedExitTime(float exitTime)
        {
            if (_currentState == null || _currentState.FrameRate <= 0f) return false;

            float duration = _currentState.TotalFrames / _currentState.FrameRate;
            if (duration <= 0f) return false;

            if (_currentState.IsLooping)
            {
                // A looping clip never produces a normalized time of exactly 1 because
                // normalized time wraps back to zero. Use accumulated clip time so an
                // exit time of 1 means "after one full loop".
                return _currentStateTime / duration >= Mathf.Max(0f, exitTime);
            }

            return GetNormalizedTime(_currentState, _currentStateTime) >= Mathf.Max(0f, exitTime);
        }

        private bool EvaluateCondition(VATAnimatorConditionData condition)
        {
            VATAnimatorParameterData parameter = FindParameterData(condition.parameterId);
            RuntimeParameterValue value = FindParameterValueById(condition.parameterId);
            if (parameter == null || value == null) return false;

            switch (parameter.type)
            {
                case VATAnimatorParameterType.Trigger:
                    return value.TriggerValue;
                case VATAnimatorParameterType.Bool:
                    return condition.mode == VATAnimatorConditionMode.IfNot
                        ? !value.BoolValue
                        : value.BoolValue;
                case VATAnimatorParameterType.Float:
                    switch (condition.mode)
                    {
                        case VATAnimatorConditionMode.Greater: return value.FloatValue > condition.threshold;
                        case VATAnimatorConditionMode.Less: return value.FloatValue < condition.threshold;
                        case VATAnimatorConditionMode.Equals: return Mathf.Approximately(value.FloatValue, condition.threshold);
                        case VATAnimatorConditionMode.NotEquals: return !Mathf.Approximately(value.FloatValue, condition.threshold);
                        default: return false;
                    }
                case VATAnimatorParameterType.Vector2:
                    float magnitude = value.Vector2Value.magnitude;
                    float thresholdMagnitude = condition.vectorThreshold.magnitude;
                    switch (condition.mode)
                    {
                        case VATAnimatorConditionMode.MagnitudeGreater: return magnitude > thresholdMagnitude;
                        case VATAnimatorConditionMode.MagnitudeLess: return magnitude < thresholdMagnitude;
                        case VATAnimatorConditionMode.Equals: return (value.Vector2Value - condition.vectorThreshold).sqrMagnitude <= 0.0001f;
                        case VATAnimatorConditionMode.NotEquals: return (value.Vector2Value - condition.vectorThreshold).sqrMagnitude > 0.0001f;
                        default: return false;
                    }
                default:
                    return false;
            }
        }

        private void ConsumeTransitionTriggers(VATAnimatorTransitionData transition)
        {
            if (transition == null || transition.conditions == null) return;
            for (int i = 0; i < transition.conditions.Count; i++)
            {
                VATAnimatorConditionData condition = transition.conditions[i];
                VATAnimatorParameterData parameter = condition == null
                    ? null
                    : FindParameterData(condition.parameterId);
                if (parameter != null && parameter.type == VATAnimatorParameterType.Trigger)
                {
                    RuntimeParameterValue value = FindParameterValueById(parameter.id);
                    if (value != null) value.TriggerValue = false;
                }
            }
        }

        private bool TryResolveTransitionTarget(
            VATAnimatorTransitionData transition,
            out VATClipInfo targetClip,
            out int targetBlendTreeId)
        {
            targetClip = null;
            targetBlendTreeId = -1;
            if (transition == null || _vatAssetData == null) return false;

            if (transition.toBlendTreeId > 0)
            {
                VATAnimatorBlendTreeData tree = FindBlendTree(transition.toBlendTreeId);
                targetClip = ResolveBlendTreeClip(tree);
                targetBlendTreeId = targetClip == null ? -1 : transition.toBlendTreeId;
                return targetClip != null;
            }

            if (transition.toStateHash != 0)
            {
                targetClip = _vatAssetData.GetClip(transition.toStateHash);
            }
            return targetClip != null;
        }

        private void UpdateCurrentBlendTreeSelection()
        {
            if (_currentBlendTreeId < 0) return;
            VATAnimatorBlendTreeData tree = FindBlendTree(_currentBlendTreeId);
            VATClipInfo selectedClip = ResolveBlendTreeClip(tree);
            if (selectedClip == null || _currentState == null ||
                selectedClip.StateHash == _currentState.StateHash) return;

            CrossFadeInternal(selectedClip, 0.05f, _currentBlendTreeId);
        }

        private VATClipInfo ResolveBlendTreeClip(VATAnimatorBlendTreeData tree)
        {
            if (tree == null || tree.children == null || tree.children.Count == 0 || _vatAssetData == null)
            {
                return null;
            }

            RuntimeParameterValue parameterValue = FindParameterValueById(tree.parameterId);
            VATAnimatorParameterData parameter = FindParameterData(tree.parameterId);
            bool useVector2 = tree.mode == VATAnimatorBlendTreeMode.TwoDimensional ||
                (parameter != null && parameter.type == VATAnimatorParameterType.Vector2);
            float floatInput = parameterValue != null
                ? parameterValue.FloatValue
                : parameter == null ? 0f : parameter.defaultFloat;
            Vector2 vectorInput = parameterValue != null
                ? parameterValue.Vector2Value
                : parameter == null ? Vector2.zero : parameter.defaultVector2;

            if (!useVector2 && tree.clampInput)
            {
                float min = float.PositiveInfinity;
                float max = float.NegativeInfinity;
                for (int i = 0; i < tree.children.Count; i++)
                {
                    VATAnimatorBlendChildData child = tree.children[i];
                    if (child == null) continue;
                    min = Mathf.Min(min, child.threshold.x);
                    max = Mathf.Max(max, child.threshold.x);
                }
                if (min != float.PositiveInfinity) floatInput = Mathf.Clamp(floatInput, min, max);
            }

            VATAnimatorBlendChildData closestChild = null;
            float closestDistance = float.PositiveInfinity;
            for (int i = 0; i < tree.children.Count; i++)
            {
                VATAnimatorBlendChildData child = tree.children[i];
                if (child == null) continue;

                float distance = useVector2
                    ? (child.threshold - vectorInput).sqrMagnitude
                    : Mathf.Abs(child.threshold.x - floatInput);
                if (closestChild == null || distance < closestDistance)
                {
                    closestChild = child;
                    closestDistance = distance;
                }
            }

            if (closestChild == null) return null;
            int stateHash = ResolveBlendChildStateHash(closestChild);
            return stateHash == 0 ? null : _vatAssetData.GetClip(stateHash);
        }

        private int ResolveBlendChildStateHash(VATAnimatorBlendChildData child)
        {
            if (child == null) return 0;
            if (child.stateHash != 0) return child.stateHash;

            string clipKey = child.clipKey;
            if (string.IsNullOrEmpty(clipKey)) return 0;
            int separator = clipKey.IndexOf(':');
            if (separator > 0)
            {
                int stateHash;
                if (int.TryParse(clipKey.Substring(0, separator), out stateHash)) return stateHash;
            }
            return VATClipInfo.GenerateHash(clipKey);
        }

        private float GetNormalizedTime(VATAnimStateData state, float time)
        {
            if (state == null || state.TotalFrames <= 0 || state.FrameRate <= 0f) return 0f;
            float duration = state.TotalFrames / state.FrameRate;
            return state.IsLooping
                ? Mathf.Repeat(time, duration) / duration
                : Mathf.Clamp01(time / duration);
        }

        // ========================================
        // Internal — Shader Property Management
        // ========================================

        private void InitializeShaderPropertyIds()
        {
            _frameIndexLowerId = Shader.PropertyToID("_FrameIndexLower");
            _frameIndexUpperId = Shader.PropertyToID("_FrameIndexUpper");
            _blendWeightId = Shader.PropertyToID("_BlendWeight");
        }

        private void ApplyVATAssetData()
        {
            if (_frameIndexLowerId == 0) InitializeShaderPropertyIds();
            if (_vatAssetData == null) return;

            if (_meshFilter == null) _meshFilter = GetComponent<MeshFilter>();
            if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();
            if (_meshRenderer == null) return;

            if (_meshFilter != null && _vatAssetData.BakedStaticMesh != null)
            {
                _meshFilter.sharedMesh = _vatAssetData.BakedStaticMesh;
            }

            if (_meshRenderer != null && _vatAssetData.BakedMaterials != null &&
                _vatAssetData.BakedMaterials.Count > 0)
            {
                _meshRenderer.sharedMaterials = _vatAssetData.BakedMaterials.ToArray();
            }

            EnsurePropertyBlocks(GetBakedMaterialCount());
            for (int materialIndex = 0; materialIndex < _propertyBlocks.Length; materialIndex++)
            {
                MaterialPropertyBlock propertyBlock = _propertyBlocks[materialIndex];
                // VAT texture, bounds and layout are immutable for an asset and
                // are serialized on the shared baked Material. Only animation
                // state belongs in the per-renderer block; this keeps identical
                // VAT instances eligible for GPU instancing.
                propertyBlock.Clear();
                propertyBlock.SetFloat(_frameIndexLowerId, _currentFrameLower);
                propertyBlock.SetFloat(_frameIndexUpperId, _currentFrameUpper);
                propertyBlock.SetFloat(_blendWeightId, _currentBlendWeight);
                _meshRenderer.SetPropertyBlock(propertyBlock, materialIndex);
            }

            // Re-assert the renderer state after asset binding. This also
            // recovers from a stale prefab/culling state that disabled the
            // renderer before the VAT asset was loaded.
            _meshRenderer.enabled = _isVisible;
        }

        private void UpdateShaderFrames(int frameLower, int frameUpper, float blendWeight)
        {
            _currentFrameLower = frameLower;
            _currentFrameUpper = frameUpper;
            _currentBlendWeight = blendWeight;

            if (_meshRenderer == null || _propertyBlocks == null) return;

            for (int materialIndex = 0; materialIndex < _propertyBlocks.Length; materialIndex++)
            {
                MaterialPropertyBlock propertyBlock = _propertyBlocks[materialIndex];
                propertyBlock.SetFloat(_frameIndexLowerId, frameLower);
                propertyBlock.SetFloat(_frameIndexUpperId, frameUpper);
                propertyBlock.SetFloat(_blendWeightId, blendWeight);
                _meshRenderer.SetPropertyBlock(propertyBlock, materialIndex);
            }

            for (int i = 0; i < _weaponRenderComponents.Count; i++)
            {
                VATWeaponRenderComponent weapon = _weaponRenderComponents[i];
                if (weapon != null)
                {
                    weapon.ApplyFrame(frameLower, frameUpper, blendWeight);
                }
            }
        }

        private int GetBakedMaterialCount()
        {
            return _vatAssetData != null && _vatAssetData.BakedMaterials != null &&
                   _vatAssetData.BakedMaterials.Count > 0
                ? _vatAssetData.BakedMaterials.Count
                : 1;
        }

        private void EnsurePropertyBlocks(int materialCount)
        {
            if (_propertyBlocks != null && _propertyBlocks.Length == materialCount) return;

            _propertyBlocks = new MaterialPropertyBlock[materialCount];
            for (int i = 0; i < materialCount; i++)
            {
                _propertyBlocks[i] = new MaterialPropertyBlock();
            }
        }
    }

}
