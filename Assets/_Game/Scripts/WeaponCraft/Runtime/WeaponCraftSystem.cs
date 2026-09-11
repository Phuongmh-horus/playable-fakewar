using System;
using System.Collections;
using System.Collections.Generic;
using GamePlay.ComponentSystems;
using UnityEngine;

namespace WeaponCraft
{
    /// <summary>
    /// Manages weapon item state and craft/merge logic.
    /// Public API unchanged: ReceiveItem(), EnsureStarterItem(), Prewarm().
    /// </summary>
    public sealed class WeaponCraftSystem : MonoSingleton<WeaponCraftSystem>
    {
        // ── Inspector ─────────────────────────────────────────────────────────────
        [Header("Craft Settings")]
        [SerializeField] private WeaponCraftConfigSO config;
        [SerializeField] private WeaponCraftVisualSystem visualSystem;
        [SerializeField] private EffectComponent upgradeEffectComponent;
        [SerializeField] private AudioClipName fallbackMergeSfx = AudioClipName.SFX_Merge_Weapon;

        // ── Runtime state ─────────────────────────────────────────────────────────
        // Sorted list: index 0 = highest tier = equipped.
        private readonly List<WeaponItem> _items = new List<WeaponItem>(16);

        // Pending items enqueued via ReceiveItem, drained each process tick.
        private readonly Queue<PendingItem> _pending = new Queue<PendingItem>(16);

        // Stable-sort sequence (lower = older = appears after newer items of same tier).
        private readonly Dictionary<WeaponItem, int> _seqMap = new Dictionary<WeaponItem, int>(32);
        private int _nextSeq;

        private Coroutine _processRoutine;
        private int _equippedTopTier = -1;
        private readonly ItemComparer _comparer = new ItemComparer();
        private readonly List<PendingItem> _batchBuffer = new List<PendingItem>(16);
        private readonly List<List<int>> _milestoneBuffer = new List<List<int>>(8);
        private readonly Stack<List<int>> _milestoneTierPool = new Stack<List<int>>(8);
        private readonly Dictionary<int, int> _tierCountsBuffer = new Dictionary<int, int>(8);

        // ── Events / Properties ───────────────────────────────────────────────────
        public event Action<WeaponItem> ItemAdded;

        public List<WeaponItem> Items => _items;
        public bool HasItems => _items.Count > 0;
        public bool IsProcessing => _processRoutine != null;
        public WeaponCraftConfigSO Config => config;
        public WeaponCraftVisualSystem VisualSystem => visualSystem;

        // ── Unity lifecycle ───────────────────────────────────────────────────────
        protected override void Awake()
        {
            _comparer.Owner = this;
            base.Awake();
            EnsureVisualSystem();
        }

        private void OnEnable()
        {
            EnsureVisualSystem();
            // Sync UI to current data (handles scene reload / re-enable).
            visualSystem?.SyncVisuals(_items);
            TryStartProcessing();
        }

        private void OnDisable()
        {
            if (_processRoutine != null)
            {
                StopCoroutine(_processRoutine);
                _processRoutine = null;
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (visualSystem == null)
                visualSystem = GetComponentInChildren<WeaponCraftVisualSystem>(true);
        }
#endif

        // ── Public API ────────────────────────────────────────────────────────────

        public WeaponItem ReceiveItem(WeaponItem item, Vector3 flyFrom)
        {
            if (item == null) return null;
            var clone = item.Clone();
            Enqueue(clone, flyFrom);
            ItemAdded?.Invoke(clone);
            return clone;
        }

        public WeaponItem ReceiveItem(WeaponItem item)
            => ReceiveItem(item, transform.position);

        public WeaponItem ReceiveItem(int tier, Vector3 flyFrom)
        {
            var item = new WeaponItem(Mathf.Clamp(tier, 1, GetMaxTier()));
            Enqueue(item, flyFrom);
            ItemAdded?.Invoke(item);
            return item;
        }

        public void ReceiveItem(int tier, Vector3 flyFrom, int count)
        {
            int safeTier = Mathf.Clamp(tier, 1, GetMaxTier());
            for (int i = 0; i < Mathf.Max(1, count); i++)
            {
                var item = new WeaponItem(safeTier);
                Enqueue(item, flyFrom);
                ItemAdded?.Invoke(item);
            }
        }

        public WeaponItem GetFirstItemOrDefault()
            => _items.Count > 0 ? _items[0] : null;

        /// <summary>Ensures at least one weapon exists. Called by GameplayManager on boot.</summary>
        public WeaponItem EnsureStarterItem()
        {
            if (_items.Count > 0) return _items[0];

            var starter = new WeaponItem(1);
            AddSequence(starter);
            _items.Add(starter);
            _items.Sort(_comparer);
            EnsureVisualSystem();
            visualSystem?.AddInstant(starter, 0);
            ItemAdded?.Invoke(starter);
            NotifyTopChanged();
            return starter;
        }

        public void Prewarm() => visualSystem?.PrewarmWeapons();

        // ── Internal ──────────────────────────────────────────────────────────────

        private void Enqueue(WeaponItem item, Vector3 flyFrom)
        {
            AddSequence(item);
            _pending.Enqueue(new PendingItem(item, flyFrom));
            TryStartProcessing();
        }

        private void TryStartProcessing()
        {
            if (!isActiveAndEnabled || _processRoutine != null || _pending.Count == 0) return;
            _processRoutine = StartCoroutine(ProcessLoop());
        }

        private IEnumerator ProcessLoop()
        {
            int slotCount = visualSystem != null ? visualSystem.SlotCount : 6;

            while (_pending.Count > 0)
            {
                _batchBuffer.Clear();
                if (_batchBuffer.Capacity < _pending.Count)
                {
                    _batchBuffer.Capacity = _pending.Count;
                }
                while (_pending.Count > 0) _batchBuffer.Add(_pending.Dequeue());

                ReleaseMilestoneBuffer();
                int currentTopTier = _items.Count > 0 ? _items[0].Tier : 0;

                // 1. Drain all pending into _items.
                for (int i = 0; i < _batchBuffer.Count; i++) _items.Add(_batchBuffer[i].Item);
                _items.Sort(_comparer);

                // Always add the initial milestone (Add phase).
                AddMilestone(slotCount);

                // 2. Resolve merges tier by tier.
                int mergeCount = GetMergeCount();
                int maxTier = GetMaxTier();
                while (true)
                {
                    int tier = FindLowestMergeable(mergeCount, maxTier);
                    if (tier < 0) break;

                    int removedCount = 0;
                    for (int i = _items.Count - 1; i >= 0 && removedCount < mergeCount; i--)
                    {
                        WeaponItem source = _items[i];
                        if (source.Tier != tier)
                        {
                            continue;
                        }

                        _items.RemoveAt(i);
                        _seqMap.Remove(source);
                        removedCount++;
                    }

                    // Create result
                    var result = new WeaponItem(tier + 1);
                    AddSequence(result);
                    _items.Add(result);
                    _items.Sort(_comparer);

                    // If top tier changed, record a milestone!
                    if (_items[0].Tier > currentTopTier)
                    {
                        currentTopTier = _items[0].Tier;
                        AddMilestone(slotCount);
                    }
                }

                EnsureVisualSystem();
                if (visualSystem != null)
                    yield return visualSystem.PlayMilestones(_batchBuffer[0].FlyFrom, _milestoneBuffer);

                visualSystem?.SyncVisuals(_items);
                NotifyTopChanged();
            }
            ReleaseMilestoneBuffer();
            _processRoutine = null;
        }

        private void AddMilestone(int slotCount)
        {
            List<int> tiers = _milestoneTierPool.Count > 0
                ? _milestoneTierPool.Pop()
                : new List<int>(slotCount);
            tiers.Clear();
            if (tiers.Capacity < slotCount)
            {
                tiers.Capacity = slotCount;
            }
            for (int i = 0; i < slotCount; i++)
            {
                tiers.Add(i < _items.Count ? _items[i].Tier : 0);
            }
            _milestoneBuffer.Add(tiers);
        }

        private void ReleaseMilestoneBuffer()
        {
            for (int i = 0; i < _milestoneBuffer.Count; i++)
            {
                List<int> milestone = _milestoneBuffer[i];
                milestone.Clear();
                _milestoneTierPool.Push(milestone);
            }
            _milestoneBuffer.Clear();
        }

        private int FindLowestMergeable(int mergeCount, int maxTier)
        {
            _tierCountsBuffer.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                int t = _items[i].Tier;
                _tierCountsBuffer.TryGetValue(t, out int count);
                _tierCountsBuffer[t] = count + 1;
            }
            int lowest = int.MaxValue;
            foreach (var pair in _tierCountsBuffer)
                if (pair.Key < maxTier && pair.Value >= mergeCount && pair.Key < lowest)
                    lowest = pair.Key;
            return lowest == int.MaxValue ? -1 : lowest;
        }

        // ── Equip / Effects ───────────────────────────────────────────────────────

        /// <summary>Called by WeaponCraftVisualSystem after each merge animation completes.</summary>
        public void OnMergeAnimationCompleted(WeaponItem mergedItem)
        {
            if (mergedItem == null) return;
            bool isUpgrade = mergedItem.Tier > _equippedTopTier;
            if (isUpgrade)
            {
                if (!PlayLocalUpgradeEffect())
                {
                    if (fallbackMergeSfx != AudioClipName.None)
                        SoundManager.Instance?.TryPlayOneShot(fallbackMergeSfx);
                    GameplayManager.Instance?.RunUpgradeEffectAt(transform.position, transform);
                }
            }
            TryEquip(mergedItem);
        }

        private void NotifyTopChanged()
        {
            if (_items.Count > 0) TryEquip(_items[0]);
        }

        private void TryEquip(WeaponItem item)
        {
            if (item == null) return;
            int tier = Mathf.Max(1, item.Tier);
            if (tier <= _equippedTopTier) return;
            _equippedTopTier = tier;
            GameplayManager.Instance?.SetMainWeapon(item);
        }

        private bool PlayLocalUpgradeEffect()
        {
            if (upgradeEffectComponent == null)
                upgradeEffectComponent = GetComponentInChildren<EffectComponent>(true);
            if (upgradeEffectComponent == null) return false;
            upgradeEffectComponent.PlayEffect(EffectType.Upgrade, transform.position, transform.rotation, transform, 0f);
            return true;
        }

        // ── Visual bootstrap ──────────────────────────────────────────────────────

        private void EnsureVisualSystem()
        {
            if (upgradeEffectComponent == null)
                upgradeEffectComponent = GetComponentInChildren<EffectComponent>(true);

            if (visualSystem == null)
                visualSystem = GetComponentInChildren<WeaponCraftVisualSystem>(true);

            if (visualSystem == null)
            {
                var go = new GameObject("WeaponCraftVisual");
                go.transform.SetParent(transform, false);
                visualSystem = go.AddComponent<WeaponCraftVisualSystem>();
            }

            visualSystem.OnMergeCompleted -= OnMergeAnimationCompleted;
            visualSystem.OnMergeCompleted += OnMergeAnimationCompleted;
            visualSystem.Bind(config);
        }

        // ── Sequence helpers ──────────────────────────────────────────────────────

        private void AddSequence(WeaponItem item)
        {
            if (item == null || _seqMap.ContainsKey(item)) return;
            _seqMap[item] = _nextSeq++;
        }

        internal int GetSequence(WeaponItem item)
            => item != null && _seqMap.TryGetValue(item, out int s) ? s : int.MaxValue;

        // ── Config ────────────────────────────────────────────────────────────────

        private int GetMergeCount() => config != null ? config.MergeCount : 3;

        private int GetMaxTier()
        {
            int max = config != null ? config.MaxTier : 1;
            if (config?.TierVisuals != null)
                for (int i = 0; i < config.TierVisuals.Count; i++)
                    if (config.TierVisuals[i] != null && config.TierVisuals[i].Tier > max)
                        max = config.TierVisuals[i].Tier;



            return Mathf.Max(1, max);
        }

        // ── Comparer ──────────────────────────────────────────────────────────────

        private sealed class ItemComparer : IComparer<WeaponItem>
        {
            public WeaponCraftSystem Owner;
            public int Compare(WeaponItem x, WeaponItem y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null) return 1;
                if (y == null) return -1;
                int tc = y.Tier.CompareTo(x.Tier);
                return tc != 0 ? tc : Owner.GetSequence(x).CompareTo(Owner.GetSequence(y));
            }
        }

        // ── Nested types ──────────────────────────────────────────────────────────

        private readonly struct PendingItem
        {
            public readonly WeaponItem Item;
            public readonly Vector3 FlyFrom;
            public PendingItem(WeaponItem i, Vector3 f) { Item = i; FlyFrom = f; }
        }

        // ── Editor helpers ────────────────────────────────────────────────────────
#if UNITY_EDITOR
        [ContextMenu("Test Receive 1 Item")]
        private void TestOne() => ReceiveItem(1, GetScreenCenter());

        [ContextMenu("Test Receive 3 Items")]
        private void TestThree() => ReceiveItem(1, GetScreenCenter(), 3);

        private Vector3 GetScreenCenter()
        {
            var cam = Camera.main;
            if (cam == null) return transform.position;
            float d = Vector3.Dot(transform.position - cam.transform.position, cam.transform.forward);
            if (d <= cam.nearClipPlane) d = Mathf.Max(1f, cam.nearClipPlane + 1f);
            return cam.ScreenToWorldPoint(new Vector3(Screen.width * .5f, Screen.height * .5f, d));
        }
#endif
    }
}
