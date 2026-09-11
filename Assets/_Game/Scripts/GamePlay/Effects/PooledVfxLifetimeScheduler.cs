using Pools;
using UnityEngine;

namespace GamePlay.Effects
{
    public static class PooledVfxLifetimeScheduler
    {
        private const int MaxActiveEntries = 24;
        private const int MaxActiveImpactEntries = 12;
        private const int MaxPendingSfxReplays = 16;
        private struct Entry
        {
            public GameObject Vfx;
            public float ExpireTime;
            public bool IsImpact;
        }

        private struct SfxReplayEntry
        {
            public AudioClip Clip;
            public float Volume;
            public float PlayTime;
        }

        private static Entry[] _activeEntries = new Entry[MaxActiveEntries];
        private static SfxReplayEntry[] _pendingSfxReplays = new SfxReplayEntry[MaxPendingSfxReplays];
        private static int _count = 0;
        private static int _impactCount;
        private static int _pendingSfxReplayCount;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeState()
        {
            System.Array.Clear(_activeEntries, 0, _activeEntries.Length);
            System.Array.Clear(_pendingSfxReplays, 0, _pendingSfxReplays.Length);
            _count = 0;
            _impactCount = 0;
            _pendingSfxReplayCount = 0;
        }

        public static bool CanSchedule(bool isImpact = false)
        {
            return _count < MaxActiveEntries && (!isImpact || _impactCount < MaxActiveImpactEntries);
        }

        public static void Schedule(GameObject vfx, float lifetime, bool isImpact = false)
        {
            if (vfx == null)
            {
                return;
            }

            // A pooled instance can be returned/reused before its previous lifetime
            // expires. Refresh its entry so an old schedule cannot despawn the reuse.
            for (int i = 0; i < _count; i++)
            {
                if (_activeEntries[i].Vfx != vfx) continue;

                if (_activeEntries[i].IsImpact != isImpact)
                {
                    if (isImpact && _impactCount >= MaxActiveImpactEntries)
                    {
                        vfx.Despawn();
                        return;
                    }

                    _impactCount += isImpact ? 1 : -1;
                    _impactCount = Mathf.Max(0, _impactCount);
                }

                _activeEntries[i].ExpireTime = Time.time + Mathf.Max(0.05f, lifetime);
                _activeEntries[i].IsImpact = isImpact;
                return;
            }

            if (!CanSchedule(isImpact))
            {
                vfx.Despawn();
                return;
            }

            _activeEntries[_count++] = new Entry
            {
                Vfx = vfx,
                ExpireTime = Time.time + Mathf.Max(0.05f, lifetime),
                IsImpact = isImpact
            };

            if (isImpact)
            {
                _impactCount++;
            }
        }

        public static void ScheduleSfxReplay(AudioClip clip, float volume, float delay)
        {
            if (clip == null)
            {
                return;
            }

            // Coalesce repeated loop requests for the same clip. The immediate sound
            // already played; retaining one replay avoids an audio burst and unbounded
            // clip references while many targets are hit together.
            for (int i = 0; i < _pendingSfxReplayCount; i++)
            {
                if (_pendingSfxReplays[i].Clip != clip) continue;

                _pendingSfxReplays[i].Volume = Mathf.Max(_pendingSfxReplays[i].Volume, Mathf.Clamp01(volume));
                return;
            }

            if (_pendingSfxReplayCount >= MaxPendingSfxReplays) return;

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

                if (entry.IsImpact)
                {
                    _impactCount = Mathf.Max(0, _impactCount - 1);
                }

                _count--;
                if (i < _count)
                {
                    _activeEntries[i] = _activeEntries[_count];
                }
                _activeEntries[_count].Vfx = null;
                _activeEntries[_count].IsImpact = false;
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
