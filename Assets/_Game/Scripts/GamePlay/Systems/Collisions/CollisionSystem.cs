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
        private const int CleanupTickInterval = 30;
        public static CollisionSystem Instance { get; private set; }

        // Managed data only
        private readonly List<IHitable> _targets = new List<IHitable>(1024);
        private readonly List<Transform> _transforms = new List<Transform>(1024);
        private readonly List<uint> _masks = new List<uint>(1024);
        private readonly List<ColliderData> _colliders = new List<ColliderData>(1024);
        private readonly List<Vector2Int> _spatialCells = new List<Vector2Int>(1024);
        private readonly Dictionary<IHitable, int> _targetIndices = new Dictionary<IHitable, int>(1024);
        private readonly Dictionary<Transform, IHitable> _transformTargets = new Dictionary<Transform, IHitable>(1024);
        private readonly Dictionary<Vector2Int, List<int>> _spatialBuckets = new Dictionary<Vector2Int, List<int>>(64);
        private readonly Stack<List<int>> _spatialBucketPool = new Stack<List<int>>(64);

        private float _maxHorizontalColliderExtent;
        private int _nextCleanupFrame;

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

        public static void NotifyMoved(IHitable target)
        {
            if (Instance == null || IsUnityNull(target)) return;
            if (Instance._targetIndices.TryGetValue(target, out int index))
            {
                Instance.UpdateSpatialCell(index);
            }
        }

        public static void NotifyMovedBatch<T>(IList<T> targets) where T : IHitable
        {
            if (Instance == null || targets == null) return;

            for (int i = 0; i < targets.Count; i++)
            {
                T target = targets[i];
                if (IsUnityNull(target)) continue;
                if (Instance._targetIndices.TryGetValue(target, out int index))
                {
                    Instance.UpdateSpatialCell(index);
                }
            }
        }

        public static void NotifyMoved(Transform transform)
        {
            if (Instance == null || transform == null) return;
            if (Instance._transformTargets.TryGetValue(transform, out var target))
            {
                NotifyMoved(target);
            }
        }

        public static void NotifyColliderChanged(IHitable target)
        {
            if (Instance == null || IsUnityNull(target)) return;
            if (Instance._targetIndices.TryGetValue(target, out int index))
            {
                Instance.RefreshTargetData(index);
            }
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
                return _maxHorizontalColliderExtent;
            }
        }

        /// <summary>
        /// Fills a caller-owned buffer with registry indices near a swept XZ segment.
        /// Cell membership persists across frames and only changes when an entity crosses
        /// a cell boundary, avoiding a complete grid rebuild for every local query.
        /// </summary>
        public void QueryIndicesNearSegment(Vector3 from, Vector3 to, float padding, List<int> results)
        {
            if (results == null) return;

            results.Clear();
            if (_targets.Count == 0) return;

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
                    var previousTransform = _transforms[existingIndex];
                    if (previousTransform != null && previousTransform != tr &&
                        _transformTargets.TryGetValue(previousTransform, out var previousTarget) &&
                        ReferenceEquals(previousTarget, target))
                    {
                        _transformTargets.Remove(previousTransform);
                    }
                    _transforms[existingIndex] = tr;
                    _transformTargets[tr] = target;
                    _masks[existingIndex] = 1u << (int)target.EntityType;
                    _colliders[existingIndex] = target.GetColliderData();
                    UpdateMaxHorizontalColliderExtent(_colliders[existingIndex]);
                    UpdateSpatialCell(existingIndex);
                    return;
                }

                _targetIndices.Remove(target);
            }

            _targets.Add(target);
            _transforms.Add(tr);
            _masks.Add(1u << (int)target.EntityType);

            var colData = target.GetColliderData();
            _colliders.Add(colData);
            _spatialCells.Add(GetSpatialCell(tr.position));
            _targetIndices[target] = _targets.Count - 1;
            _transformTargets[tr] = target;
            AddToSpatialBucket(_spatialCells[_spatialCells.Count - 1], _targets.Count - 1);
            UpdateMaxHorizontalColliderExtent(colData);
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
                return;
            }

            // Fallback keeps compatibility with entries created before the index map existed.
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_targets[i], target) && !Equals(_targets[i], target)) continue;
                RemoveAtSwapBack(i);
                return;
            }
        }

        private void RemoveAllTargets()
        {
            _targets.Clear();
            _transforms.Clear();
            _masks.Clear();
            _colliders.Clear();
            _spatialCells.Clear();
            _targetIndices.Clear();
            _transformTargets.Clear();
            ClearSpatialBuckets();
            _maxHorizontalColliderExtent = 0f;
        }

        // ================= UPDATE LOGIC =================

        /// <summary>
        /// Kept for compatibility with the original Jobs-based API. Registry updates are
        /// incremental, so callers no longer trigger a full target scan every frame.
        /// </summary>
        public void EnsureDataIsReady()
        {
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
        }

        /// <summary>
        /// Explicit recovery API for exceptional global collider-data changes. Normal
        /// movement and lifecycle paths use NotifyMoved and NotifyColliderChanged.
        /// </summary>
        public void ManualUpdate()
        {
            if (Time.frameCount < _nextCleanupFrame)
            {
                return;
            }

            _nextCleanupFrame = Time.frameCount + CleanupTickInterval;
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var t = _targets[i];
                var tr = _transforms[i];
                if (IsUnityNull(t) || tr == null || IsUnityNull(tr))
                {
                    RemoveAtSwapBack(i);
                }
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
            var removedTransform = _transforms[index];
            float removedExtent = GetHorizontalColliderExtent(_colliders[index]);
            if (removedTarget != null)
            {
                _targetIndices.Remove(removedTarget);
            }
            if (removedTransform != null && _transformTargets.TryGetValue(removedTransform, out var mappedTarget) &&
                ReferenceEquals(mappedTarget, removedTarget))
            {
                _transformTargets.Remove(removedTransform);
            }
            RemoveFromSpatialBucket(_spatialCells[index], index);

            if (index != last)
            {
                _targets[index] = _targets[last];
                _transforms[index] = _transforms[last];
                _masks[index] = _masks[last];
                _colliders[index] = _colliders[last];
                _spatialCells[index] = _spatialCells[last];

                var movedTarget = _targets[index];
                if (movedTarget != null)
                {
                    _targetIndices[movedTarget] = index;
                }
                var movedTransform = _transforms[index];
                if (movedTransform != null)
                {
                    _transformTargets[movedTransform] = movedTarget;
                }
                ReplaceSpatialBucketIndex(_spatialCells[index], last, index);
            }

            _targets.RemoveAt(last);
            _transforms.RemoveAt(last);
            _masks.RemoveAt(last);
            _colliders.RemoveAt(last);
            _spatialCells.RemoveAt(last);

            if (removedExtent >= _maxHorizontalColliderExtent)
            {
                RecalculateMaxHorizontalColliderExtent();
            }
        }

        public static Vector2Int GetSpatialCell(Vector3 position)
        {
            return new Vector2Int(
                Mathf.FloorToInt(position.x / SpatialCellSize),
                Mathf.FloorToInt(position.z / SpatialCellSize));
        }

        private void UpdateSpatialCell(int index)
        {
            Vector2Int nextCell = GetSpatialCell(_transforms[index].position);
            if (_spatialCells[index] == nextCell) return;

            RemoveFromSpatialBucket(_spatialCells[index], index);
            _spatialCells[index] = nextCell;
            AddToSpatialBucket(nextCell, index);
        }

        private void RefreshTargetData(int index)
        {
            if (index < 0 || index >= _targets.Count) return;

            var target = _targets[index];
            if (IsUnityNull(target)) return;

            _masks[index] = 1u << (int)target.EntityType;
            float previousExtent = GetHorizontalColliderExtent(_colliders[index]);
            ColliderData collider = target.GetColliderData();
            _colliders[index] = collider;
            if (previousExtent >= _maxHorizontalColliderExtent && GetHorizontalColliderExtent(collider) < previousExtent)
            {
                RecalculateMaxHorizontalColliderExtent();
            }
            UpdateMaxHorizontalColliderExtent(collider);
            UpdateSpatialCell(index);
        }

        private void AddToSpatialBucket(Vector2Int cell, int index)
        {
            if (!_spatialBuckets.TryGetValue(cell, out var bucket))
            {
                bucket = _spatialBucketPool.Count > 0 ? _spatialBucketPool.Pop() : new List<int>(8);
                _spatialBuckets.Add(cell, bucket);
            }
            bucket.Add(index);
        }

        private void RemoveFromSpatialBucket(Vector2Int cell, int index)
        {
            if (!_spatialBuckets.TryGetValue(cell, out var bucket)) return;

            for (int i = bucket.Count - 1; i >= 0; i--)
            {
                if (bucket[i] != index) continue;
                int last = bucket.Count - 1;
                bucket[i] = bucket[last];
                bucket.RemoveAt(last);
                break;
            }

            if (bucket.Count != 0) return;
            _spatialBuckets.Remove(cell);
            _spatialBucketPool.Push(bucket);
        }

        private void ReplaceSpatialBucketIndex(Vector2Int cell, int oldIndex, int newIndex)
        {
            if (!_spatialBuckets.TryGetValue(cell, out var bucket)) return;

            for (int i = 0; i < bucket.Count; i++)
            {
                if (bucket[i] != oldIndex) continue;
                bucket[i] = newIndex;
                return;
            }
        }

        private void UpdateMaxHorizontalColliderExtent(ColliderData collider)
        {
            _maxHorizontalColliderExtent = Mathf.Max(_maxHorizontalColliderExtent, GetHorizontalColliderExtent(collider));
        }

        private void RecalculateMaxHorizontalColliderExtent()
        {
            _maxHorizontalColliderExtent = 0f;
            for (int i = 0; i < _colliders.Count; i++)
            {
                UpdateMaxHorizontalColliderExtent(_colliders[i]);
            }
        }

        private static float GetHorizontalColliderExtent(ColliderData collider)
        {
            return Mathf.Max(Mathf.Abs(collider.Size.x), Mathf.Abs(collider.Size.z));
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
