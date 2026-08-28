using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Pools
{
    public static class PoolSystem
    {
        private const string RootName = "[Pools]";
        private static Transform _root;

        private class Pool
        {
            public readonly Stack<IPoolable> Inactive = new Stack<IPoolable>();
            public readonly HashSet<IPoolable> Active = new HashSet<IPoolable>();
            public Component PrefabComponent;
            public Transform Root;
        }

        private static readonly Dictionary<int, Pool> Pools = new Dictionary<int, Pool>(64);
        private static readonly Dictionary<IPoolable, Pool> PoolByInstance = new Dictionary<IPoolable, Pool>(1024);
        private static readonly Dictionary<int, Pool> PoolByGameObjectId = new Dictionary<int, Pool>(1024);
        private static readonly Dictionary<int, IPoolable> PoolableByGameObjectId = new Dictionary<int, IPoolable>(1024);

        public static void ClearAllPools()
        {
            Pools.Clear();
            PoolByInstance.Clear();
            PoolByGameObjectId.Clear();
            PoolableByGameObjectId.Clear();
            if (_root != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    Object.DestroyImmediate(_root.gameObject);
                else
#endif
                    Object.Destroy(_root.gameObject);
                _root = null;
            }
        }

        // [FIX] Chỉ còn DUY NHẤT 1 method generic, không còn overload GameObject/Component song song
        public static void Prewarm<T>(T prefab, int count) where T : Component
        {
            if (prefab == null || count <= 0) return;
            if (!Application.isPlaying) return;

            var pool = GetOrCreatePool(prefab);

            for (int i = 0; i < count; i++)
            {
                PrewarmOne(pool);
            }
        }

        public static IEnumerator PrewarmAsync<T>(T prefab, int count, int maxPerFrame) where T : Component
        {
            if (prefab == null || count <= 0) yield break;
            if (!Application.isPlaying) yield break;

            var pool = GetOrCreatePool(prefab);
            int batchSize = Mathf.Max(1, maxPerFrame);

            for (int i = 0; i < count; i++)
            {
                PrewarmOne(pool);

                if ((i + 1) % batchSize == 0)
                    yield return null;
            }
        }

        public static IEnumerator EnsurePrewarmAsync<T>(T prefab, int inactiveCount, int maxPerFrame) where T : Component
        {
            if (prefab == null || inactiveCount <= 0) yield break;
            if (!Application.isPlaying) yield break;

            var pool = GetOrCreatePool(prefab);
            int missingCount = Mathf.Max(0, inactiveCount - pool.Inactive.Count);
            int batchSize = Mathf.Max(1, maxPerFrame);

            for (int i = 0; i < missingCount; i++)
            {
                PrewarmOne(pool);

                if ((i + 1) % batchSize == 0)
                    yield return null;
            }
        }

        public static int GetInactiveCount<T>(T prefab) where T : Component
        {
            if (prefab == null) return 0;

            int key = prefab.gameObject.GetInstanceID();
            return Pools.TryGetValue(key, out var pool) ? pool.Inactive.Count : 0;
        }

        public static bool IsPooled(Component component)
        {
            if (component == null) return false;

            IPoolable poolable = component as IPoolable;
            if (poolable == null || !PoolByInstance.ContainsKey(poolable))
            {
                poolable = FindPoolableInParents(component.transform);
            }

            return poolable != null && PoolByInstance.ContainsKey(poolable);
        }

        public static T Spawn<T>(T prefab, Vector3 pos, Quaternion rot, Transform parent = null) where T : Component
        {
            if (prefab == null) return null;

            if (!Application.isPlaying)
            {
                var go = Object.Instantiate(prefab.gameObject, parent);
                var comp = go.GetComponent<T>();
                var editTr = go.transform;
                editTr.SetPositionAndRotation(pos, rot);
                go.SetActive(true);
                (comp as IPoolable)?.New();
                return comp;
            }

            var obj = SpawnInternal(prefab, parent) as T;
            if (obj == null) return null;

            var runtimeTr = obj.transform;
            if (parent != null && runtimeTr.parent != parent)
            {
                runtimeTr.SetParent(parent, false);
            }
            runtimeTr.SetPositionAndRotation(pos, rot);

            obj.gameObject.SetActive(true);
            (obj as IPoolable)?.New();

            return obj;
        }

        public static T TrySpawn<T>(T prefab, Vector3 pos, Quaternion rot, Transform parent = null) where T : Component
        {
            if (prefab == null || !Application.isPlaying) return null;

            var pool = GetOrCreatePool(prefab);
            if (pool.Inactive.Count == 0) return null;

            var obj = SpawnInternal(prefab, parent, false) as T;
            if (obj == null) return null;

            var runtimeTr = obj.transform;
            if (parent != null && runtimeTr.parent != parent)
            {
                runtimeTr.SetParent(parent, false);
            }

            runtimeTr.SetPositionAndRotation(pos, rot);
            obj.gameObject.SetActive(true);
            (obj as IPoolable)?.New();
            return obj;
        }

        public static T Spawn<T>(T prefab, Transform parent) where T : Component
        {
            if (prefab == null) return null;

            if (!Application.isPlaying)
            {
                var go = Object.Instantiate(prefab.gameObject, parent);
                var comp = go.GetComponent<T>();
                var editTr = go.transform;
                editTr.localPosition = Vector3.zero;
                editTr.localRotation = Quaternion.identity;
                editTr.localScale = Vector3.one;
                go.SetActive(true);
                (comp as IPoolable)?.New();
                return comp;
            }

            var obj = SpawnInternal(prefab, parent) as T;
            if (obj == null) return null;

            var runtimeTr = obj.transform;
            if (parent != null && runtimeTr.parent != parent)
            {
                runtimeTr.SetParent(parent, false);
            }
            runtimeTr.localPosition = Vector3.zero;
            runtimeTr.localRotation = Quaternion.identity;
            runtimeTr.localScale = Vector3.one;

            obj.gameObject.SetActive(true);
            (obj as IPoolable)?.New();

            return obj;
        }

        private static Component SpawnInternal(Component prefab, Transform parent, bool allowInstantiate = true)
        {
            if (prefab == null) return null;

            var pool = GetOrCreatePool(prefab);

            IPoolable instance = null;
            if (pool.Inactive.Count > 0)
            {
                instance = pool.Inactive.Pop();
            }
            else
            {
                if (!allowInstantiate) return null;

                var go = Object.Instantiate(pool.PrefabComponent.gameObject, pool.Root);
                instance = go.GetComponent<IPoolable>();
                if (instance == null) instance = go.AddComponent<GenericPoolable>();
            }

            pool.Active.Add(instance);
            PoolByInstance[instance] = pool;
            PoolByGameObjectId[((Component)instance).gameObject.GetInstanceID()] = pool;
            PoolableByGameObjectId[((Component)instance).gameObject.GetInstanceID()] = instance;
            return ((Component)instance).gameObject.GetComponent(prefab.GetType());
        }

        private static Pool GetOrCreatePool(Component prefab)
        {
            int key = prefab.gameObject.GetInstanceID();
            if (Pools.TryGetValue(key, out var pool))
                return pool;

            var root = GetOrCreateRoot();
            var poolName = $"[Pool]_{prefab.name}";

            pool = new Pool
            {
                PrefabComponent = prefab,
                Root = new GameObject(poolName).transform
            };

            if (root != null && pool.Root.parent != root)
                pool.Root.SetParent(root, false);

            Pools[key] = pool;
            return pool;
        }

        private static void PrewarmOne(Pool pool)
        {
            GameObject go = null;
            try
            {
                go = Object.Instantiate(pool.PrefabComponent.gameObject, pool.Root);
            }
            catch { return; }

            if (go == null) return;

            var instance = go.GetComponent<IPoolable>();
            if (instance == null) instance = go.AddComponent<GenericPoolable>();
            PoolByInstance[instance] = pool;
            PoolByGameObjectId[go.GetInstanceID()] = pool;
            PoolableByGameObjectId[go.GetInstanceID()] = instance;
            go.SetActive(false);
            pool.Inactive.Push(instance);
        }

        public static void Despawn(Component poolableComp)
        {
            if (poolableComp == null) return;

            IPoolable poolable = poolableComp as IPoolable;
            if (poolable == null)
                poolable = poolableComp.GetComponent<IPoolable>();

            if (poolable == null || !PoolByInstance.ContainsKey(poolable))
            {
                poolable = FindPoolableInParents(poolableComp.transform);
            }

            Pool pool;
            Component poolableComponent = poolable as Component;
            if (poolableComponent == null ||
                !PoolByGameObjectId.TryGetValue(poolableComponent.gameObject.GetInstanceID(), out pool))
            {
                if (!Application.isPlaying)
                {
                    Object.DestroyImmediate(poolableComp.gameObject);
                }
                else
                {
                    // Optimize: Avoid Destroy() to prevent GC Allocs (e.g. TMP_Text.OnDestroy) during gameplay.
                    poolableComp.gameObject.SetActive(false);
                }
                return;
            }

            if (!Application.isPlaying)
            {
                Object.DestroyImmediate(poolableComp.gameObject);
                return;
            }

            if (!pool.Active.Remove(poolable))
            {
                return;
            }

            poolable.Free();

            poolableComponent.gameObject.SetActive(false);
            poolableComponent.transform.SetParent(pool.Root, false);

            pool.Inactive.Push(poolable);
        }

        private static IPoolable FindPoolableInParents(Transform transform)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                IPoolable poolable = current.GetComponent<IPoolable>();
                if (poolable != null && PoolByInstance.ContainsKey(poolable))
                {
                    return poolable;
                }
            }

            return null;
        }

        public static Transform GetRoot()
        {
            return GetOrCreateRoot();
        }

        private static Transform GetOrCreateRoot()
        {
            if (_root != null) return _root;

            var rootGo = new GameObject(RootName);
            _root = rootGo.transform;
            return _root;
        }
    }
}
