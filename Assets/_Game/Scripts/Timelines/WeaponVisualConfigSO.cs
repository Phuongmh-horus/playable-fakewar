using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "NewWeaponVisualConfig", menuName = "Game Config/Weapon Visual Config", order = 5)]
public class WeaponVisualConfigSO : ScriptableObject
{
    [System.Serializable]
    public class WeaponVisualEntry
    {
        [Tooltip("Sát thương cơ bản của vũ khí tại cấp độ này")]
        public int BaseDamage = 1;

        [Tooltip("Tốc độ bay của đạn vũ khí (nếu có)")]
        public float ProjectileSpeed = 10f;

        [Tooltip("Khoảng cách bay tối đa của vũ khí")]
        public float FireRange = 6f;

        [Tooltip("Mesh của vũ khí (thay đổi hình dạng)")]
        public Mesh WeaponMesh;

        [Tooltip("Material của vũ khí (thay đổi màu sắc/texture)")]
        public Material WeaponMaterial;
        
        [Tooltip("Prefab đạn (tuỳ chọn - nếu muốn thay đổi cả logic prefab của viên đạn)")]
        public GameObject ProjectilePrefabOverride;
    }

    [Header("Danh sách Asset Vũ Khí theo Cấp độ (Index = Level)")]
    [Tooltip("Cấp độ sẽ tương ứng với chỉ số index (0, 1, 2...)")]
    public List<WeaponVisualEntry> Levels = new List<WeaponVisualEntry>();

    public WeaponVisualEntry GetEntry(int index)
    {
        if (Levels == null || Levels.Count == 0)
        {
            return null;
        }
        int clampedIndex = Mathf.Clamp(index, 0, Levels.Count - 1);
        return Levels[clampedIndex];
    }
}
