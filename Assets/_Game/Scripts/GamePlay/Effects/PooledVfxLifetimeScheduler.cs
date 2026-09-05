using Pools;
using UnityEngine;

namespace GamePlay.Effects
{
    public static class PooledVfxLifetimeScheduler
    {
        private const int MaxActiveEntries = 24;
        private const int InitialSfxReplayCapacity = 16;
        private struct Entry
        {
            public GameObject Vfx;
            public float ExpireTime;
        }

        private struct SfxReplayEntry
        {
            public AudioClip Clip;
            public float Volume;
            public float PlayTime;
        }

        private static Entry[] _activeEntries = new Entry[64];
        private static SfxReplayEntry[] _pendingSfxReplays = new SfxReplayEntry[InitialSfxReplayCapacity];
        private static int _count = 0;
        private static int _pendingSfxReplayCount;

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

        public static void ScheduleSfxReplay(AudioClip clip, float volume, float delay)
        {
            if (clip == null)
            {
                return;
            }

            if (_pendingSfxReplayCount >= _pendingSfxReplays.Length)
            {
                System.Array.Resize(ref _pendingSfxReplays, _pendingSfxReplays.Length * 2);
            }

            _pendingSfxReplays[_pendingSfxReplayCount++] = new SfxReplayEntry
            {
                Clip = clip,
                Volume = Mathf.Clamp01(volume),
                PlayTime = Time.time + Mathf.Max(0.01f, delay)
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

            for (int i = _pendingSfxReplayCount - 1; i >= 0; i--)
            {
                ref SfxReplayEntry entry = ref _pendingSfxReplays[i];
                if (currentTime < entry.PlayTime)
                {
                    continue;
                }

                SoundManager.Instance?.PlayOneShot(entry.Clip, entry.Volume);

                _pendingSfxReplayCount--;
                if (i < _pendingSfxReplayCount)
                {
                    _pendingSfxReplays[i] = _pendingSfxReplays[_pendingSfxReplayCount];
                }
                _pendingSfxReplays[_pendingSfxReplayCount].Clip = null;
            }
        }
    }
}
