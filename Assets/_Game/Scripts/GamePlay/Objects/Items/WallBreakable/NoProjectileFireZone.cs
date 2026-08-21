using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GamePlay.Items
{
    [DisallowMultipleComponent]
    public sealed class NoProjectileFireZone : MonoBehaviour
    {
        private struct ActiveZone
        {
            public int OwnerId;
            public Scene Scene;
            public float MinX;
            public float MaxX;
        }

        private static readonly List<ActiveZone> ActiveZones = new List<ActiveZone>(8);

        [Header("World-Space Bounds")]
        [SerializeField] private float minX;
        [SerializeField] private float maxX;
        [SerializeField] private bool activeOnStart;

        public bool IsActiveZone { get; private set; }

        public static bool Contains(Vector3 position)
        {
            for (int i = ActiveZones.Count - 1; i >= 0; i--)
            {
                var zone = ActiveZones[i];
                if (!zone.Scene.isLoaded)
                {
                    ActiveZones.RemoveAt(i);
                    continue;
                }

                if (position.x >= zone.MinX && position.x <= zone.MaxX)
                {
                    return true;
                }
            }

            return false;
        }

        public void Activate()
        {
            if (IsActiveZone)
            {
                return;
            }

            IsActiveZone = true;
            int ownerId = GetInstanceID();
            RemoveZone(ownerId);
            ActiveZones.Add(new ActiveZone
            {
                OwnerId = ownerId,
                Scene = gameObject.scene,
                MinX = Mathf.Min(minX, maxX),
                MaxX = Mathf.Max(minX, maxX)
            });
        }

        public void Deactivate()
        {
            IsActiveZone = false;
            RemoveZone(GetInstanceID());
        }

        private bool ContainsPosition(Vector3 position)
        {
            float lowerX = Mathf.Min(minX, maxX);
            float upperX = Mathf.Max(minX, maxX);
            return position.x >= lowerX && position.x <= upperX;
        }

        private static void RemoveZone(int ownerId)
        {
            for (int i = ActiveZones.Count - 1; i >= 0; i--)
            {
                if (ActiveZones[i].OwnerId == ownerId)
                {
                    ActiveZones.RemoveAt(i);
                }
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            float lowerX = Mathf.Min(minX, maxX);
            float upperX = Mathf.Max(minX, maxX);
            Vector3 center = new Vector3((lowerX + upperX) * 0.5f, transform.position.y, transform.position.z);
            Vector3 size = new Vector3(upperX - lowerX, 0.05f, 0.5f);
            Gizmos.color = IsActiveZone ? new Color(1f, 0.3f, 0.1f, 0.75f) : new Color(1f, 0.85f, 0.1f, 0.5f);
            Gizmos.DrawWireCube(center, size);
        }
#endif
    }
}
