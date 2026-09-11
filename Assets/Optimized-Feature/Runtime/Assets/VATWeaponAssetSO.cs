using System.Collections.Generic;
using UnityEngine;

namespace OptimizedFeature.Scripts
{
    /// <summary>
    /// VAT payload for an optional item renderer that shares the body frame manifest.
    /// An item asset has its own mesh, bounds, materials and position texture,
    /// while StartFrame/EndFrame values remain aligned with the body asset.
    /// </summary>
    [CreateAssetMenu(fileName = "VATItemAsset", menuName = "VAT/VAT Item Asset")]
    public class VATWeaponAssetSO : ScriptableObject
    {
        private static readonly Material[] EmptyMaterials = new Material[0];

        public Texture2D VATTexture;
        public Mesh BakedStaticMesh;
        public Vector3 BoundingMin;
        public Vector3 BoundingMax;
        public int TotalVertices;
        public int TotalFrames;
        public List<VATClipInfo> Clips = new List<VATClipInfo>();
        public List<Material> BakedMaterials = new List<Material>();

        private Dictionary<int, VATClipInfo> _clipHashCache;
        [System.NonSerialized] private Material[] _bakedMaterialArray;

        private void OnEnable()
        {
            _clipHashCache = null;
            _bakedMaterialArray = null;
            if (Clips == null) Clips = new List<VATClipInfo>();
            if (BakedMaterials == null) BakedMaterials = new List<Material>();
        }

        private void OnValidate()
        {
            if (Clips == null) Clips = new List<VATClipInfo>();
            if (BakedMaterials == null) BakedMaterials = new List<Material>();
            for (int i = 0; i < Clips.Count; i++)
            {
                if (Clips[i] != null)
                {
                    Clips[i].StateHash = VATClipInfo.GenerateHash(Clips[i].ClipName);
                }
            }
            _clipHashCache = null;
            _bakedMaterialArray = null;
        }

        public Material[] GetBakedMaterialArray()
        {
            int count = BakedMaterials == null ? 0 : BakedMaterials.Count;
            bool rebuild = _bakedMaterialArray == null || _bakedMaterialArray.Length != count;
            if (!rebuild)
            {
                for (int i = 0; i < count; i++)
                {
                    if (_bakedMaterialArray[i] != BakedMaterials[i])
                    {
                        rebuild = true;
                        break;
                    }
                }
            }

            if (rebuild)
            {
                _bakedMaterialArray = count == 0 ? EmptyMaterials : BakedMaterials.ToArray();
            }

            return _bakedMaterialArray;
        }

        public VATClipInfo GetClip(int stateHash)
        {
            if (_clipHashCache == null || _clipHashCache.Count != Clips.Count)
            {
                _clipHashCache = new Dictionary<int, VATClipInfo>(Clips.Count);
                for (int i = 0; i < Clips.Count; i++)
                {
                    VATClipInfo clip = Clips[i];
                    if (clip != null) _clipHashCache[clip.StateHash] = clip;
                }
            }

            VATClipInfo result;
            return _clipHashCache.TryGetValue(stateHash, out result) ? result : null;
        }
    }
}
