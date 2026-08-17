using UnityEngine;

namespace OptimizedFeature.Scripts
{
    /// <summary>
    /// Optional weapon/sub-render VAT channel driven by the parent body's frame
    /// state. It never advances its own clock, which guarantees Body and Weapon
    /// textures sample the same clip frame during playback and cross-fade.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class VATWeaponRenderComponent : MonoBehaviour
    {
        private static readonly int VatTextureId = Shader.PropertyToID("_VATTex");
        private static readonly int BoundingMinId = Shader.PropertyToID("_BoundingMin");
        private static readonly int BoundingMaxId = Shader.PropertyToID("_BoundingMax");
        private static readonly int NumFramesId = Shader.PropertyToID("_NumFrames");
        private static readonly int NumVerticesId = Shader.PropertyToID("_NumVertices");
        private static readonly int FrameIndexLowerId = Shader.PropertyToID("_FrameIndexLower");
        private static readonly int FrameIndexUpperId = Shader.PropertyToID("_FrameIndexUpper");
        private static readonly int BlendWeightId = Shader.PropertyToID("_BlendWeight");

        [SerializeField] private MeshFilter _meshFilter;
        [SerializeField] private MeshRenderer _meshRenderer;
        [SerializeField] private string _weaponName = "Weapon";
        [SerializeField] private VATWeaponAssetSO _weaponAsset;
        [SerializeField] private VAT_RenderComponent _frameSource;

        private MaterialPropertyBlock[] _propertyBlocks;
        private bool _isVisible = true;
        private int _currentFrameLower = -1;
        private int _currentFrameUpper = -1;
        private float _currentBlendWeight = -1f;

        public VATWeaponAssetSO WeaponAsset => _weaponAsset;
        public string WeaponName => _weaponName;
        public MeshRenderer Renderer => _meshRenderer;

        private void Awake()
        {
            EnsureComponents();
            if (_frameSource == null)
            {
                _frameSource = GetComponentInParent<VAT_RenderComponent>();
            }

            ApplyWeaponAsset();
        }

        public void SetFrameSource(VAT_RenderComponent frameSource)
        {
            _frameSource = frameSource;
        }

        public void SetWeaponName(string weaponName)
        {
            _weaponName = string.IsNullOrWhiteSpace(weaponName) ? "Weapon" : weaponName.Trim();
        }

        public void SetWeaponAsset(VATWeaponAssetSO weaponAsset)
        {
            if (weaponAsset != null && _frameSource != null &&
                !_frameSource.IsWeaponFrameManifestCompatible(weaponAsset))
            {
                int bodyFrames = _frameSource.VatAssetData == null
                    ? 0
                    : _frameSource.VatAssetData.TotalFrames;
                Debug.LogError(
                    $"[VATWeaponRenderComponent] Weapon VAT '{weaponAsset.name}' does not share " +
                    $"the Body VAT frame manifest (expected {bodyFrames} frames).",
                    gameObject);
                return;
            }

            _weaponAsset = weaponAsset;
            ApplyWeaponAsset();
        }

        public void SetVisibility(bool visible)
        {
            if (_isVisible == visible)
            {
                return;
            }

            _isVisible = visible;
            if (_meshRenderer != null)
            {
                _meshRenderer.enabled = visible && _weaponAsset != null;
            }
        }

        public void ApplyFrame(int frameLower, int frameUpper, float blendWeight)
        {
            if (_weaponAsset == null || _meshRenderer == null || _propertyBlocks == null)
            {
                return;
            }

            if (_currentFrameLower == frameLower &&
                _currentFrameUpper == frameUpper &&
                Mathf.Approximately(_currentBlendWeight, blendWeight))
            {
                return;
            }

            _currentFrameLower = frameLower;
            _currentFrameUpper = frameUpper;
            _currentBlendWeight = blendWeight;

            for (int materialIndex = 0; materialIndex < _propertyBlocks.Length; materialIndex++)
            {
                MaterialPropertyBlock propertyBlock = _propertyBlocks[materialIndex];
                propertyBlock.SetFloat(FrameIndexLowerId, frameLower);
                propertyBlock.SetFloat(FrameIndexUpperId, frameUpper);
                propertyBlock.SetFloat(BlendWeightId, blendWeight);
                _meshRenderer.SetPropertyBlock(propertyBlock, materialIndex);
            }
        }

        public Bounds GetWorldBounds()
        {
            if (_weaponAsset == null)
            {
                return new Bounds(transform.position, Vector3.zero);
            }

            Vector3 min = _weaponAsset.BoundingMin;
            Vector3 max = _weaponAsset.BoundingMax;
            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;
            Vector3 worldCenter = transform.TransformPoint(center);
            Vector3 scale = transform.lossyScale;
            Vector3 worldSize = new Vector3(
                size.x * Mathf.Abs(scale.x),
                size.y * Mathf.Abs(scale.y),
                size.z * Mathf.Abs(scale.z));
            return new Bounds(worldCenter, worldSize);
        }

        private void EnsureComponents()
        {
            if (_meshFilter == null) _meshFilter = GetComponent<MeshFilter>();
            if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();
        }

        private void ApplyWeaponAsset()
        {
            EnsureComponents();
            if (_meshFilter == null || _meshRenderer == null)
            {
                return;
            }

            if (_weaponAsset == null)
            {
                _meshFilter.sharedMesh = null;
                _meshRenderer.sharedMaterials = new Material[0];
                _meshRenderer.enabled = false;
                _propertyBlocks = null;
                return;
            }

            _meshFilter.sharedMesh = _weaponAsset.BakedStaticMesh;
            if (_weaponAsset.BakedMaterials != null && _weaponAsset.BakedMaterials.Count > 0)
            {
                _meshRenderer.sharedMaterials = _weaponAsset.BakedMaterials.ToArray();
            }

            int materialCount = _weaponAsset.BakedMaterials != null && _weaponAsset.BakedMaterials.Count > 0
                ? _weaponAsset.BakedMaterials.Count
                : 1;
            EnsurePropertyBlocks(materialCount);
            for (int materialIndex = 0; materialIndex < _propertyBlocks.Length; materialIndex++)
            {
                MaterialPropertyBlock propertyBlock = _propertyBlocks[materialIndex];
                propertyBlock.SetTexture(VatTextureId, _weaponAsset.VATTexture);
                propertyBlock.SetVector(BoundingMinId, _weaponAsset.BoundingMin);
                propertyBlock.SetVector(BoundingMaxId, _weaponAsset.BoundingMax);
                propertyBlock.SetFloat(NumFramesId, _weaponAsset.TotalFrames);
                propertyBlock.SetFloat(NumVerticesId, _weaponAsset.TotalVertices);
                _meshRenderer.SetPropertyBlock(propertyBlock, materialIndex);
            }

            _meshRenderer.enabled = _isVisible;
        }

        private void EnsurePropertyBlocks(int materialCount)
        {
            if (_propertyBlocks != null && _propertyBlocks.Length == materialCount)
            {
                return;
            }

            _propertyBlocks = new MaterialPropertyBlock[materialCount];
            for (int i = 0; i < materialCount; i++)
            {
                _propertyBlocks[i] = new MaterialPropertyBlock();
            }
        }
    }
}
