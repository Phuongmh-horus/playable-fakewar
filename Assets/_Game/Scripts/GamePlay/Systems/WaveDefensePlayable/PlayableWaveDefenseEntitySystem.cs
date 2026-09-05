using System.Collections.Generic;
using GamePlay.CollisionSystems;
using GamePlay.Entities;
using GamePlay.Items;
using GamePlay.Enemies;
using Pools;
using UnityEngine;

[DisallowMultipleComponent]
public class PlayableWaveDefenseEntitySystem : MonoBehaviour
{
    public static PlayableWaveDefenseEntitySystem Instance { get; private set; }

    [Header("Movement")]
    [SerializeField, Min(0f)] private float moveSpeed = 8f;
    [SerializeField, Min(0f)] private float gateMoveSpeed = 8f;
    [SerializeField, Min(0f)] private float rotationSpeed = 5f;

    [Header("Homing")]
    [SerializeField, Min(0f)] private float attractionThreshold = 10f;
    [SerializeField] private float despawnZOffset = -20f;

    [Header("Completion")]
    [SerializeField] private bool endGameWhenAllMovingEntitiesCleared = false;

    private readonly List<Entry> _entries = new List<Entry>(256);
    private readonly List<WallBreakable> _movingGateBlockers = new List<WallBreakable>(32);
    private readonly Dictionary<ItemUnit, WallBreakable> _movingGateBlockerCache = new Dictionary<ItemUnit, WallBreakable>(32);
    private Transform _playerTransform;
    private bool _registeredThisRun;
    private bool _completedThisRun;

    private struct Entry
    {
        public ItemUnit Item;
        public Transform Transform;
        public float MoveSpeed;
        public bool IsAttractive;
        public Vector2Int CollisionCell;
    }

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
        if (Instance == this)
        {
            Instance = null;
        }
    }

    public static PlayableWaveDefenseEntitySystem EnsureInstance()
    {
        if (Instance != null)
        {
            return Instance;
        }

        var go = new GameObject("PlayableWaveDefenseEntitySystem (Auto-Created)");
        return go.AddComponent<PlayableWaveDefenseEntitySystem>();
    }

    public void RegisterFromGeneratedObjects(IList<ItemUnit> generatedObjects, Transform playerTransform)
    {
        Clear();
        _playerTransform = playerTransform;
        _registeredThisRun = true;
        _completedThisRun = false;

        if (generatedObjects == null)
        {
            return;
        }

        for (int i = 0; i < generatedObjects.Count; i++)
        {
            RegisterItem(generatedObjects[i]);
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _movingGateBlockers.Clear();
        _movingGateBlockerCache.Clear();
        _registeredThisRun = false;
        _completedThisRun = false;
    }

    public void ManualUpdate()
    {
        if (_entries.Count == 0)
        {
            CompleteIfNeeded();
            return;
        }

        Vector3 playerPos = _playerTransform != null ? _playerTransform.position : Vector3.zero;
        float playerZ = playerPos.z;
        float dt = Time.deltaTime;

        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            Entry entry = _entries[i];
            if (entry.Item == null || entry.Transform == null || !entry.Item.gameObject.activeInHierarchy)
            {
                RemoveAtSwapBack(i);
                continue;
            }

            Vector3 currentPos = entry.Transform.position;
            Vector3 targetDir = Vector3.back;

            if (entry.Item.EntityType == EntityType.MovingGate && TryBlockMovingGate(entry, ref currentPos))
            {
                if (entry.Transform.position != currentPos)
                {
                    entry.Transform.position = currentPos;
                    NotifyCollisionCellChanged(ref entry, currentPos);
                    _entries[i] = entry;
                }
                continue;
            }

            if (entry.IsAttractive && _playerTransform != null)
            {
                Vector3 toPlayer = playerPos - currentPos;
                toPlayer.y = 0f;
                float entryAttractionThreshold = attractionThreshold;
                if (entry.Item is BossUnit bossUnit)
                {
                    entryAttractionThreshold = bossUnit.AttractionThreshold;
                }

                if (toPlayer.sqrMagnitude <= entryAttractionThreshold * entryAttractionThreshold && toPlayer.sqrMagnitude > 0.0001f)
                {
                    targetDir = toPlayer.normalized;
                    Quaternion targetRot = Quaternion.LookRotation(targetDir, Vector3.up);
                    entry.Transform.rotation = Quaternion.Slerp(entry.Transform.rotation, targetRot, rotationSpeed * dt);
                }
            }

            currentPos += targetDir * entry.MoveSpeed * dt;
            entry.Transform.position = currentPos;
            NotifyCollisionCellChanged(ref entry, currentPos);

            if (currentPos.z < playerZ + despawnZOffset)
            {
                var go = entry.Item.gameObject;
                RemoveAtSwapBack(i);
                go.Despawn();
                continue;
            }

            _entries[i] = entry;
        }

        CompleteIfNeeded();
    }

    public void RegisterItem(ItemUnit item)
    {
        if (item == null || !item.gameObject.activeInHierarchy)
        {
            return;
        }

        if (item is WallBreakable wallBreakable)
        {
            RegisterMovingGateBlocker(wallBreakable);
        }

        if (ShouldSkipMovement(item))
        {
            return;
        }

        bool isGate = item.EntityType == EntityType.MovingGate;
        bool isPowerGate = item.EntityType == EntityType.PowerGate;
        bool isAttractive = item.EntityType == EntityType.Enemy || item.EntityType == EntityType.Boss;

        _entries.Add(new Entry
        {
            Item = item,
            Transform = item.Transform,
            MoveSpeed = isGate ? gateMoveSpeed : (isPowerGate ? moveSpeed + 3f : moveSpeed),
            IsAttractive = isAttractive,
            CollisionCell = CollisionSystem.GetSpatialCell(item.Transform.position)
        });
    }

    private static void NotifyCollisionCellChanged(ref Entry entry, Vector3 position)
    {
        Vector2Int nextCell = CollisionSystem.GetSpatialCell(position);
        if (entry.CollisionCell == nextCell)
        {
            return;
        }

        entry.CollisionCell = nextCell;
        entry.Item.NotifyCollisionPositionChanged();
    }

    public void Unregister(ItemUnit item)
    {
        if (item == null)
        {
            return;
        }

        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            Entry entry = _entries[i];
            if (entry.Item == item)
            {
                RemoveAtSwapBack(i);
            }
        }

        if (item is WallBreakable wallBreakable)
        {
            _movingGateBlockers.Remove(wallBreakable);
            _movingGateBlockerCache.Clear();
        }

        _movingGateBlockerCache.Remove(item);
    }

    private void RegisterMovingGateBlocker(WallBreakable wallBreakable)
    {
        if (wallBreakable == null || !wallBreakable.BlocksMovingGates)
        {
            return;
        }

        if (!_movingGateBlockers.Contains(wallBreakable))
        {
            _movingGateBlockers.Add(wallBreakable);
        }
    }

    private bool TryBlockMovingGate(Entry movingGate, ref Vector3 currentPos)
    {
        if (movingGate.Transform == null || _movingGateBlockers.Count == 0)
        {
            return false;
        }

        WallBreakable nearestBlocker;
        if (!_movingGateBlockerCache.TryGetValue(movingGate.Item, out nearestBlocker) ||
            !IsValidMovingGateBlocker(nearestBlocker, currentPos))
        {
            nearestBlocker = null;
            float nearestForwardDistance = float.MaxValue;

            for (int i = _movingGateBlockers.Count - 1; i >= 0; i--)
            {
                WallBreakable blocker = _movingGateBlockers[i];
                if (blocker == null || !blocker.BlocksMovingGates || !blocker.gameObject.activeInHierarchy)
                {
                    _movingGateBlockers.RemoveAt(i);
                    continue;
                }

                Vector3 blockerPos = blocker.transform.position;
                float laneDistance = Mathf.Abs(currentPos.x - blockerPos.x);
                if (laneDistance > blocker.MovingGateBlockHalfWidth)
                {
                    continue;
                }

                float forwardDistance = currentPos.z - blockerPos.z;
                if (forwardDistance < 0f || forwardDistance >= nearestForwardDistance)
                {
                    continue;
                }

                nearestForwardDistance = forwardDistance;
                nearestBlocker = blocker;
            }

            if (nearestBlocker == null)
            {
                _movingGateBlockerCache.Remove(movingGate.Item);
                return false;
            }

            _movingGateBlockerCache[movingGate.Item] = nearestBlocker;
        }

        float blockedZ = nearestBlocker.transform.position.z + nearestBlocker.MovingGateStopDistance;
        if (currentPos.z < blockedZ)
        {
            currentPos.z = blockedZ;
        }

        return true;
    }

    private static bool IsValidMovingGateBlocker(WallBreakable blocker, Vector3 currentPos)
    {
        if (blocker == null || !blocker.BlocksMovingGates || !blocker.gameObject.activeInHierarchy)
        {
            return false;
        }

        Vector3 blockerPos = blocker.transform.position;
        return Mathf.Abs(currentPos.x - blockerPos.x) <= blocker.MovingGateBlockHalfWidth &&
               currentPos.z >= blockerPos.z;
    }

    private static bool ShouldSkipMovement(ItemUnit item)
    {
        if (item is MultiSlotDynamicGate)
        {
            return true;
        }

        if (item is WallBreakable)
        {
            return true;
        }

        if (item.EntityType == EntityType.FinishTower && !(item is SoldierBall))
        {
            return true;
        }

        return false;
    }

    public bool IsCompleted() => _completedThisRun;
    public bool EndGameWhenAllMovingEntitiesCleared => endGameWhenAllMovingEntitiesCleared;

    private void CompleteIfNeeded()
    {
        if (!_registeredThisRun || _completedThisRun || _entries.Count > 0)
        {
            return;
        }

        _completedThisRun = true;
        // The win logic is now handled in GameplayManager to also wait for enemies to die.
    }

    private void RemoveAtSwapBack(int index)
    {
        int last = _entries.Count - 1;
        if (index < 0 || index > last)
        {
            return;
        }

        Entry removedEntry = _entries[index];
        if (removedEntry.Item != null)
        {
            _movingGateBlockerCache.Remove(removedEntry.Item);
        }

        if (index != last)
        {
            _entries[index] = _entries[last];
        }

        _entries.RemoveAt(last);
    }
}
