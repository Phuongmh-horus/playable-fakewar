using System.Collections.Generic;
using GamePlay.ComponentSystems;
using UnityEngine;

namespace GamePlay.CollisionSystems
{
    /// <summary>
    /// Playable-safe CollisionSystem:
    /// - No Unity.Collections, no Jobs, no Mathematics.
    /// - Keeps the same public API surface used by other scripts.
    /// </summary>
    [DisallowMultipleComponent]
    public class CollisionSystem : MonoBehaviour
    {
        private const float SpatialCellSize = 8f;
        public static CollisionSystem Instance { get; private set; }

        // Managed data only
        private readonly List<IHitable> _targets = new List<IHitable>();
        private readonly List<Transform> _transforms = new List<Transform>();
        private readonly List<uint> _masks = new List<uint>();
        private readonly List<ColliderData> _colliders = new List<ColliderData>();
        private readonly Dictionary<IHitable, int> _targetIndices = new Dictionary<IHitable, int>();
        private readonly Dictionary<Vector2Int, List<int>> _spatialBuckets = new Dictionary<Vector2Int, List<int>>(64);
        private readonly Stack<List<int>> _spatialBucketPool = new Stack<List<int>>(64);

        private bool _isDirty = false;
        private int _spatialIndexFrame = -1;
        private float _maxHorizontalColliderExtent;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ================= API PUBLIC =================

        public static void Register(IHitable target, Transform transform)
        {
            EnsureInstance();
            if (Instance == null)
            {
                Debug.LogError($"[CollisionSystem] Register FAILED: Instance is still NULL after EnsureInstance!");
                return;
            }
            Instance.AddTarget(target, transform);
        }

        private static void EnsureInstance()
        {
            if (Instance != null) return;

            // Create new GameObject with CollisionSystem
            var go = new GameObject("CollisionSystem (Auto-Created)");
            Instance = go.AddComponent<CollisionSystem>();
            DontDestroyOnLoad(go);
        }

        public static void Unregister(IHitable target)
        {
            if (Instance == null) return;
            Instance.RemoveTarget(target);
        }

        public static void RegisterBatch(IList<IHitable> targets, IList<Transform> transforms)
        {
            EnsureInstance();
            if (Instance == null) return;
            Instance.AddTargetsBatch(targets, transforms);
        }

        public static void UnregisterAll()
        {
            if (Instance == null) return;
            Instance.RemoveAllTargets();
        }

        /// <summary>
        /// Playable: "sorted index" is the same as insertion order index.
        /// </summary>
        public IHitable GetTargetBySortedIndex(int sortedIndex)
        {
            if (sortedIndex < 0 || sortedIndex >= _targets.Count) return null;
            return _targets[sortedIndex];
        }

        /// <summary>
        /// Optional accessors if you need them later.
        /// </summary>
        public int Count => _targets.Count;

        /// <summary>
        /// Largest horizontal collider extent in the current spatial-index frame.
        /// Queries use this to include large targets whose pivot lies outside an area.
        /// </summary>
        public float MaxHorizontalColliderExtent
        {
            get
            {
                EnsureSpatialIndex();
                return _maxHorizontalColliderExtent;
            }
        }

        /// <summary>
        /// Fills a caller-owned buffer with registry indices near a swept XZ segment.
        /// The grid is rebuilt once per frame from live transforms, so moving enemies/items
        /// never use stale positions and projectile queries do not allocate.
        /// </summary>
        public void QueryIndicesNearSegment(Vector3 from, Vector3 to, float padding, List<int> results)
        {
            if (results == null) return;

            results.Clear();
            if (_targets.Count == 0) return;

            EnsureSpatialIndex();

            float minX = Mathf.Min(from.x, to.x) - Mathf.Max(0f, padding);
            float maxX = Mathf.Max(from.x, to.x) + Mathf.Max(0f, padding);
            float minZ = Mathf.Min(from.z, to.z) - Mathf.Max(0f, padding);
            float maxZ = Mathf.Max(from.z, to.z) + Mathf.Max(0f, padding);

            int minCellX = Mathf.FloorToInt(minX / SpatialCellSize);
            int maxCellX = Mathf.FloorToInt(maxX / SpatialCellSize);
            int minCellZ = Mathf.FloorToInt(minZ / SpatialCellSize);
            int maxCellZ = Mathf.FloorToInt(maxZ / SpatialCellSize);

            for (int cellX = minCellX; cellX <= maxCellX; cellX++)
            {
                for (int cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
                {
                    if (!_spatialBuckets.TryGetValue(new Vector2Int(cellX, cellZ), out var bucket)) continue;

                    for (int i = 0; i < bucket.Count; i++)
                    {
                        int index = bucket[i];
                        var transform = GetTransform(index);
                        if (transform == null) continue;

                        Vector3 position = transform.position;
                        if (position.x < minX || position.x > maxX ||
                            position.z < minZ || position.z > maxZ)
                        {
                            continue;
                        }

                        results.Add(index);
                    }
                }
            }
        }

        // ================= INTERNAL LOGIC =================

        private void AddTarget(IHitable target, Transform tr)
        {
            if (IsUnityNull(target))
            {
                Debug.LogWarning("[CollisionSystem] AddTarget failed: target is null");
                return;
            }
            if (tr == null || IsUnityNull(tr))
            {
                Debug.LogWarning("[CollisionSystem] AddTarget failed: transform is null");
                return;
            }

            CompactInvalidEntries();

            // Prevent duplicate registration of the same IHitable (common in pooled re-init paths).
            if (_targetIndices.TryGetValue(target, out int existingIndex))
            {
                if (existingIndex >= 0 && existingIndex < _targets.Count &&
                    (ReferenceEquals(_targets[existingIndex], target) || Equals(_targets[existingIndex], target)))
                {
                    _transforms[existingIndex] = tr;
                    _masks[existingIndex] = 1u << (int)target.EntityType;
                    _colliders[existingIndex] = target.GetColliderData();
                    _isDirty = true;
                    _spatialIndexFrame = -1;
                    return;
                }

                _targetIndices.Remove(target);
            }

            _targets.Add(target);
            _transforms.Add(tr);
            _masks.Add(1u << (int)target.EntityType);

            var colData = target.GetColliderData();
            _colliders.Add(colData);
            _targetIndices[target] = _targets.Count - 1;
            _isDirty = true;
            _spatialIndexFrame = -1;
        }

        private void AddTargetsBatch(IList<IHitable> targets, IList<Transform> transforms)
        {
            if (targets == null || transforms == null || targets.Count != transforms.Count) return;

            for (int i = 0; i < targets.Count; i++)
            {
                AddTarget(targets[i], transforms[i]);
            }
        }

        private void RemoveTarget(IHitable target)
        {
            if (IsUnityNull(target)) return;

            if (_targetIndices.TryGetValue(target, out int index))
            {
                RemoveAtSwapBack(index);
                _isDirty = true;
                _spatialIndexFrame = -1;
                return;
            }

            // Fallback keeps compatibility with entries created before the index map existed.
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_targets[i], target) && !Equals(_targets[i], target)) continue;
                RemoveAtSwapBack(i);
                _isDirty = true;
                _spatialIndexFrame = -1;
                return;
            }
        }

        private void RemoveAllTargets()
        {
            _targets.Clear();
            _transforms.Clear();
            _masks.Clear();
            _colliders.Clear();
            _targetIndices.Clear();
            ClearSpatialBuckets();
            _isDirty = false;
        }

        // ================= UPDATE LOGIC =================

        /// <summary>
        /// In the Jobs version this ensured data sync/sort once per frame.
        /// Here it's kept for compatibility and can be used to "refresh" cached collider data.
        /// </summary>
        public void EnsureDataIsReady()
        {
            if (_isDirty && CompactInvalidEntries())
            {
                _isDirty = true;
            }

            if (!_isDirty) return;
            ManualUpdate();
        }

        private void EnsureSpatialIndex()
        {
            if (_spatialIndexFrame == Time.frameCount) return;

            ClearSpatialBuckets();
            _maxHorizontalColliderExtent = 0f;
            for (int i = 0; i < _transforms.Count; i++)
            {
                var transform = _transforms[i];
                if (transform == null) continue;

                var collider = _colliders[i];
                _maxHorizontalColliderExtent = Mathf.Max(
                    _maxHorizontalColliderExtent,
                    Mathf.Max(Mathf.Abs(collider.Size.x), Mathf.Abs(collider.Size.z)));

                Vector3 position = transform.position;
                var cell = new Vector2Int(
                    Mathf.FloorToInt(position.x / SpatialCellSize),
                    Mathf.FloorToInt(position.z / SpatialCellSize));

                if (!_spatialBuckets.TryGetValue(cell, out var bucket))
                {
                    bucket = _spatialBucketPool.Count > 0 ? _spatialBucketPool.Pop() : new List<int>(8);
                    _spatialBuckets.Add(cell, bucket);
                }

                bucket.Add(i);
            }

            _spatialIndexFrame = Time.frameCount;
        }

        private void ClearSpatialBuckets()
        {
            if (_spatialBuckets.Count == 0) return;

            foreach (var bucket in _spatialBuckets.Values)
            {
                bucket.Clear();
                _spatialBucketPool.Push(bucket);
            }

            _spatialBuckets.Clear();
            _spatialIndexFrame = -1;
        }

        /// <summary>
        /// Refresh cached collider data and masks from current targets.
        /// </summary>
        public void ManualUpdate()
        {
            _isDirty = false;

            // refresh collider data (in case size changes at runtime)
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var t = _targets[i];
                var tr = _transforms[i];
                if (IsUnityNull(t) || tr == null || IsUnityNull(tr))
                {
                    RemoveAtSwapBack(i);
                    continue;
                }

                _masks[i] = 1u << (int)t.EntityType;
                _colliders[i] = t.GetColliderData();
            }
        }

        private bool CompactInvalidEntries()
        {
            bool removedAny = false;
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var t = _targets[i];
                var tr = _transforms[i];
                if (!IsUnityNull(t) && tr != null && !IsUnityNull(tr)) continue;

                RemoveAtSwapBack(i);
                removedAny = true;
            }

            return removedAny;
        }

        private void RemoveAtSwapBack(int index)
        {
            int last = _targets.Count - 1;
            if (index < 0 || index > last) return;

            var removedTarget = _targets[index];
            if (removedTarget != null)
            {
                _targetIndices.Remove(removedTarget);
            }

            if (index != last)
            {
                _targets[index] = _targets[last];
                _transforms[index] = _transforms[last];
                _masks[index] = _masks[last];
                _colliders[index] = _colliders[last];

                var movedTarget = _targets[index];
                if (movedTarget != null)
                {
                    _targetIndices[movedTarget] = index;
                }
            }

            _targets.RemoveAt(last);
            _transforms.RemoveAt(last);
            _masks.RemoveAt(last);
            _colliders.RemoveAt(last);
            _spatialIndexFrame = -1;
        }

        private static bool IsUnityNull(object obj)
        {
            if (obj == null) return true;
            if (obj is Object unityObject) return unityObject == null;
            return false;
        }

        // ================= Data query helpers (optional) =================

        public Transform GetTransform(int index)
        {
            if (index < 0 || index >= _transforms.Count) return null;
            return _transforms[index];
        }

        public uint GetMask(int index)
        {
            if (index < 0 || index >= _masks.Count) return 0;
            return _masks[index];
        }

        public ColliderData GetColliderData(int index)
        {
            if (index < 0 || index >= _colliders.Count) return default;
            return _colliders[index];
        }
    }
}
