using GamePlay.Entities;
using GamePlay.Items;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GamePlay.Map
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class ContentDataLinker : MonoBehaviour
    {
        [Header("Content Link")]
        [SerializeField] private ContentDataSO sourceDataSO;
        [SerializeField] private int itemIndex = -1;
        [SerializeField] private ItemUnit originalPrefab;
        [SerializeField] private EntityType objectType = EntityType.None;

        [Header("Original Values")]
        [SerializeField] private float originalPositionOnMap;
        [SerializeField] private Vector3 originalPositionOffset;
        [SerializeField] private Vector3 originalRotation;
        [SerializeField] private Vector3 originalScale = Vector3.one;

        public ContentDataSO SourceDataSO => sourceDataSO;
        public int ItemIndex => itemIndex;
        public ItemUnit OriginalPrefab => originalPrefab;
        public EntityType ObjectType => objectType;
        public float OriginalPositionOnMap => originalPositionOnMap;
        public Vector3 OriginalPositionOffset => originalPositionOffset;
        public Vector3 OriginalRotation => originalRotation;
        public Vector3 OriginalScale => originalScale;

        public static bool IsContentEntry(ItemUnit item)
        {
            if (item == null)
            {
                return false;
            }

            MultiSlotDynamicGate parentGate = item.GetComponentInParent<MultiSlotDynamicGate>();
            return parentGate == null || parentGate == item;
        }

        public void Link(ContentDataSO source, int index, SpawnableObject spawnable, ItemUnit item)
        {
            sourceDataSO = source;
            itemIndex = index;
            originalPrefab = spawnable != null ? spawnable.Prefab : null;
            objectType = item != null ? item.EntityType : EntityType.None;

            if (spawnable != null)
            {
                originalPositionOnMap = spawnable.PositionOnMap;
                originalPositionOffset = spawnable.PositionOffset;
                originalRotation = spawnable.Rotation;
                originalScale = spawnable.Scale;
            }

#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        public void MarkDirty()
        {
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (sourceDataSO == null) return;

            GUIStyle style = new GUIStyle
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold
            };
            style.normal.textColor = Color.yellow;
            Handles.Label(transform.position + Vector3.up * 2f, $"SO: {sourceDataSO.name}\nItem: {itemIndex}\nType: {objectType}", style);
        }
#endif
    }
}
