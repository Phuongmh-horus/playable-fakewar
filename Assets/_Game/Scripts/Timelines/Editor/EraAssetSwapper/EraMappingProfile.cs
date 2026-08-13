using System.Collections.Generic;
using UnityEngine;

namespace GamePlay.Utilities.EraAssetSwapper
{
    [System.Serializable]
    public class AssetPair
    {
        [Tooltip("Asset của thời đại cũ (vd: t1_a1)")]
        public Object originalAsset;
        
        [Tooltip("Asset của thời đại mới (vd: t1_a2)")]
        public Object replacementAsset;
    }

    [CreateAssetMenu(fileName = "NewEraMappingProfile", menuName = "Age Evolution/Era Mapping Profile", order = 50)]
    public class EraMappingProfile : ScriptableObject
    {
        public List<AssetPair> mappings = new List<AssetPair>();

        /// <summary>
        /// Tìm asset thay thế tương ứng với asset gốc
        /// </summary>
        public Object GetReplacement(Object original)
        {
            if (original == null) return null;

            foreach (var pair in mappings)
            {
                if (pair.originalAsset == original && pair.replacementAsset != null)
                {
                    return pair.replacementAsset;
                }
            }
            return null;
        }
    }
}
