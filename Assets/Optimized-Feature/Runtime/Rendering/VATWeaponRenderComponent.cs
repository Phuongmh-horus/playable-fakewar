using System.Collections.Generic;
using UnityEngine;

namespace OptimizedFeature.Scripts
{
    /// <summary>
    /// Optional item/sub-render VAT channel driven by the parent body's frame
    /// state. It never advances its own clock, which guarantees Body and Item
    /// textures sample the same clip frame during playback and cross-fade.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class VATWeaponRenderComponent : MonoBehaviour
    {
        private static readonly int FrameIndexLowerId = Shader.PropertyToID("_FrameIndexLower");
        private static readonly int FrameIndexUpperId = Shader.PropertyToID("_FrameIndexUpper");
        private static readonly int BlendWeightId = Shader.PropertyToID("_BlendWeight");

        [SerializeField] private MeshFilter _meshFilter;
        [SerializeField] private MeshRenderer _meshRenderer;
        [SerializeField] private int _weaponHash;
        [SerializeField] private VATWeaponAssetSO _weaponAsset;
        [SerializeField] private VAT_RenderComponent _frameSource;

        private MaterialPropertyBlock[] _propertyBlocks;
        private bool _isVisible = true;

        public VATWeaponAssetSO WeaponAsset => _weaponAsset;
        public int WeaponHash => _weaponHash;
        public MeshRenderer Renderer => _meshRenderer;
        public bool IsVisible => _isVisible && _weaponAsset != null;

        private void Awake()
        {
            EnsureComponents();
            if (_frameSource == null)
            {
                _frameSource = GetComponentInParent<VAT_RenderComponent>();
            }

            NormalizeToFrameSourceSpace();
            ApplyWeaponAsset();
        }

        public void SetFrameSource(VAT_RenderComponent frameSource)
        {
            _frameSource = frameSource;
            if (Application.isPlaying)
            {
                NormalizeToFrameSourceSpace();
            }
        }

        public void SetWeaponHash(int weaponHash)
        {
            _weaponHash = weaponHash;
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
                    $"[VATWeaponRenderComponent] Item VAT '{weaponAsset.name}' does not share " +
                    $"the Body VAT frame manifest (expected {bodyFrames} frames).",
                    gameObject);
                return;
            }

            _weaponAsset = weaponAsset;
            ApplyWeaponAsset();
        }

        public void SetVisibility(bool visible)
        {
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

            for (int materialIndex = 0; materialIndex < _propertyBlocks.Length; materialIndex++)
            {
                MaterialPropertyBlock propertyBlock = _propertyBlocks[materialIndex];
                propertyBlock.SetFloat(FrameIndexLowerId, frameLower);
                propertyBlock.SetFloat(FrameIndexUpperId, frameUpper);
                propertyBlock.SetFloat(BlendWeightId, blendWeight);
                _meshRenderer.SetPropertyBlock(propertyBlock, materialIndex);
            }
        }

        internal void AppendRuntimeBatchSource(
            List<VATRuntimeMeshBatcher.Source> sources,
            VAT_RenderComponent frameSource)
        {
            if (sources == null || _weaponAsset == null) return;

            EnsureComponents();
            if (_meshFilter == null || _meshRenderer == null) return;

            Material[] materials = _meshRenderer.sharedMaterials;
            if (materials == null || materials.Length != 1 || materials[0] == null) return;

            sources.Add(new VATRuntimeMeshBatcher.Source
            {
                MeshFilter = _meshFilter,
                Renderer = _meshRenderer,
                Mesh = _meshFilter.sharedMesh,
                Material = materials[0],
                Owner = frameSource,
                Weapon = this,
                BoundsMin = _weaponAsset.BoundingMin,
                BoundsMax = _weaponAsset.BoundingMax,
                FrameLower = frameSource == null ? 0f : frameSource.CurrentFrameLower,
                FrameUpper = frameSource == null ? 0f : frameSource.CurrentFrameUpper,
                BlendWeight = frameSource == null ? 0f : frameSource.CurrentBlendWeight
            });
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

        private void NormalizeToFrameSourceSpace()
        {
            if (_frameSource == null || transform == _frameSource.transform)
            {
                return;
            }

            // Baked item positions are in the Body/VAT root local space.
            // Keep the child neutral so the root transform (including a
            // non-unit parent scale) is applied exactly once.
            if (transform.parent != _frameSource.transform)
            {
                transform.SetParent(_frameSource.transform, false);
            }

            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;
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
                // Immutable VAT data is stored on the shared baked Material.
                // Keep this block exclusively for per-instance animation state.
                propertyBlock.Clear();
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
