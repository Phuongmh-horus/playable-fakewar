using Pools;
using UnityEngine;

namespace GamePlay.Effects
{
    public static class PooledVfxLifetimeScheduler
    {
        private const int MaxActiveEntries = 24;
        private struct Entry
        {
            public GameObject Vfx;
            public float ExpireTime;
        }

        private static Entry[] _activeEntries = new Entry[64];
        private static int _count = 0;

        public static bool CanSchedule()
        {
            return _count < MaxActiveEntries;
        }

        public static void Schedule(GameObject vfx, float lifetime)
        {
            if (vfx == null)
            {
                return;
            }

            if (_count >= MaxActiveEntries)
            {
                vfx.Despawn();
                return;
            }

            if (_count >= _activeEntries.Length)
            {
                System.Array.Resize(ref _activeEntries, _activeEntries.Length * 2);
            }

            _activeEntries[_count++] = new Entry
            {
                Vfx = vfx,
                ExpireTime = Time.time + Mathf.Max(0.05f, lifetime)
            };
        }

        public static void Tick(float currentTime)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                ref Entry entry = ref _activeEntries[i];
                if (entry.Vfx != null && currentTime < entry.ExpireTime)
                {
                    continue;
                }

                if (entry.Vfx != null)
                {
                    entry.Vfx.Despawn();
                }

                _count--;
                if (i < _count)
                {
                    _activeEntries[i] = _activeEntries[_count];
                }
                _activeEntries[_count].Vfx = null;
            }
        }
    }
}
