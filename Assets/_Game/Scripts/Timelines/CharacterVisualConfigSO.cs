using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "NewCharacterVisualConfig", menuName = "Game Config/Character Visual Config", order = 4)]
public class CharacterVisualConfigSO : ScriptableObject
{
    [System.Serializable]
    public class CharacterVisualEntry
    {
        [Tooltip("Prefab của model nhân vật, chứa bộ xương và SkinnedMeshRenderer (để thay đổi theo cấp)")]
        public GameObject ModelPrefab;
        
        [Tooltip("Material của nhân vật nếu cần ghi đè (không bắt buộc)")]
        public Material MaterialOverride;
    }

    [Header("Danh sách Asset Nhân Vật theo Cấp độ (Index = Level)")]
    [Tooltip("Cấp độ sẽ tương ứng với chỉ số index (0, 1, 2...)")]
    public List<CharacterVisualEntry> Levels = new List<CharacterVisualEntry>();

    public CharacterVisualEntry GetEntry(int index)
    {
        if (Levels == null || Levels.Count == 0)
        {
            return null;
        }
        int clampedIndex = Mathf.Clamp(index, 0, Levels.Count - 1);
        return Levels[clampedIndex];
    }
}
