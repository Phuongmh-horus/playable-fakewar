using System;
using System.Collections;
using System.Collections.Generic;
using GamePlay.AnimationSystems;
using GamePlay.CombatSystems;
using GamePlay.ComponentSystems;
using GamePlay.Entities;
using GamePlay.Effects;
using GamePlay.Items;
using GamePlay.Weapons;
using Pools;
using UnityEngine;
using UnityEngine.Serialization;

namespace GamePlay.Characters
{
    public class CharacterUnit : PoolEntity, IHitable
    {
        public static int CharacterCount = 0;

        // [Header("Components References (Playable)")]
        // [SerializeField] private List<MonoBehaviour> components = new List<MonoBehaviour>();

        [Header("Models")]
        [SerializeField] private GameObject[] visualModels;

        [Header("Weapons")]
        [SerializeField] private Transform projectilePoint;
        [SerializeField] private Transform bodyscalable;

        [Header("Sound Effects")]
        [SerializeField] private AudioClipName attackSfx = AudioClipName.SFX_CharacterAttack;
        [SerializeField] private float maxAttackSfxPerFrame = 1;
        [SerializeField] private bool playAttackVfxOnObstacleHit = false;
        [SerializeField, Min(1)] private int maxAttackVfxPerFrame = 3;
        [SerializeField, Min(0f)] private float obstacleAttackDespawnDelay = 0.5f;
        [SerializeField, Min(0f)] private float enemyAttackDespawnDelay = 0.5f;

        [Header("Hit Settings")]
        [SerializeField] private ShapeType hitShapeType = ShapeType.Cylinder;
        [SerializeField] private Vector3 hitColliderSize = new Vector3(0.6f, 1.2f, 0.6f);

        [SerializeField] public int Level = -1;

        private int _appliedVisualLevel = -1;
        private bool _projectileTargetRegistered;
        private bool _isCountedInRuntime;
        public event Action<IAttacker> OnHitComplete;

        // [FIX] Store the original assigned index in the honeycomb formation
        public int ArmyIndex { get; set; } = -1;

        // jump properties
        private readonly IHitable[] _hitBuffer = new IHitable[5];
        private int _hitCount;

        private bool _isCombatActive = false;
        public int AttackCounter = 0;
        public float NextAttackTime { get; set; }

        [Header("Death VFX")]
        [SerializeField] private GameObject dieVfxPrefab;
        public GameObject DieVfxPrefab => dieVfxPrefab;
        [SerializeField] private Vector3 dieVfxOffset = Vector3.zero;
        [SerializeField] private float dieVfxLifetime = 1.2f;
        [SerializeField] private int maxDeathVfxPerFrame = 5;
        [SerializeField] private bool playDeathVfxOnAttackDespawn = false;
        private static float s_lastDeathVfxTime = -999f;
        private static int s_lastDeathVfxFrame = -1;
        private static int s_lastAttackSfxFrame = -1;
        private static int s_lastAttackVfxFrame = -1;
        private const int AttackEffectFrameInterval = 15;
        private bool _isAttackDespawnScheduled;

        private struct ScheduledDespawn
        {
            public CharacterUnit Unit;
            public float Time;
            public bool PlayDeathVfx;
        }

        private static readonly List<ScheduledDespawn> s_scheduledDespawns = new List<ScheduledDespawn>(128);

        public Transform ProjectilePoint => EnsureProjectilePoint();

        private static GameObject GetPooledObject(GameObject prefab)
        {
            if (prefab == null) return null;

            return prefab.Spawn();
        }

        private readonly List<Transform> _transformQueryBuffer = new List<Transform>();

        protected override void Awake()
        {
            base.Awake();

            BuildCapabilityPack();
            Level = -1;

            EnsureProjectilePoint();
            EnsureBodyScalable();
        }



        public virtual void Initialize()
        {
            // Empty base - for override
        }

        public void Initialize(int level, bool isPassive = false)
        {
            if (isPassive)
            {
                InitializePreview(level);
                return;
            }

            ResetTransientRuntimeState();
            Setup(level);

            if ((ActiveFlags & CapabilityFlags.Move) != 0) Pack.Mover.Initialize();
            if ((ActiveFlags & CapabilityFlags.Attack) != 0) Pack.Attacker.Initialize();
            if ((ActiveFlags & CapabilityFlags.Jump) != 0) Pack.Jumper.Initialize();
            if ((ActiveFlags & CapabilityFlags.Animator) != 0) Pack.Animator.Initialize();
            if ((ActiveFlags & CapabilityFlags.Heal) != 0) Pack.Healable.Initialize();

            RegisterEvents(true);

            if (!_isCountedInRuntime)
            {
                CharacterCount++;
                _isCountedInRuntime = true;
            }

            RegisterProjectileTarget();
            CombatSystem.Register(transform, Pack, ActiveFlags);
        }

        public void InitializePreview(int level)
        {
            ResetTransientRuntimeState();
            Setup(level);

            if ((ActiveFlags & CapabilityFlags.Animator) != 0)
            {
                Pack.Animator.Initialize();
                Pack.Animator.PlayAnimation(AnimationType.Idle, 0f, null, 0);
            }

        }

        private void OnDisable()
        {
            CancelScheduledDespawn();
            _isAttackDespawnScheduled = false;

            RegisterEvents(false);
            UnregisterProjectileTarget();
            ClearHits();
            _isCombatActive = false;

            if (!_isCountedInRuntime) return;
            CharacterCount = Mathf.Max(0, CharacterCount - 1);
            _isCountedInRuntime = false;
        }

        public void ApplyVisualLevel(int levelIndex)
        {
            if (visualModels == null || visualModels.Length == 0) return;

            // Limit level index to avoid out of bounds
            int safeIndex = Mathf.Clamp(levelIndex, 0, visualModels.Length - 1);
            if (_appliedVisualLevel == safeIndex)
            {
                return;
            }

            _appliedVisualLevel = safeIndex;

            for (int i = 0; i < visualModels.Length; i++)
            {
                if (visualModels[i] != null)
                {
                    bool isActive = (i == safeIndex);
                    if (visualModels[i].activeSelf != isActive)
                    {
                        visualModels[i].SetActive(isActive);
                    }

                    if (isActive)
                    {
                        if (Pack.Animator != null && Pack.Animator is AnimationComponent animComp)
                        {
                            animComp.SetAnimatorLevel(i);
                        }

                    }
                }
            }
        }

        public void Setup(int level)
        {
            Level = level;
            SetupComponents();
        }

        private void SetupComponents()
        {
            if ((ActiveFlags & CapabilityFlags.Attack) != 0)
                Pack.Attacker.Setup(1); // Base damage, modified by ArmyUpgradeManager
        }

        private void RegisterEvents(bool register)
        {
            if (register)
            {
                if (Pack.Mover != null) Pack.Mover.OnMovementComplete += HandleMovementComplete;
                if (Pack.Attacker != null) Pack.Attacker.OnAttackComplete += HandleAttackComplete;
                if (Pack.Jumper != null) Pack.Jumper.OnJumperComplete += HandleJumperComplete;
                if (Pack.Healable != null) Pack.Healable.OnHealthChange += HandleHealthChange;
            }
            else
            {
                if (Pack.Mover != null) Pack.Mover.OnMovementComplete -= HandleMovementComplete;
                if (Pack.Attacker != null) Pack.Attacker.OnAttackComplete -= HandleAttackComplete;
                if (Pack.Jumper != null) Pack.Jumper.OnJumperComplete -= HandleJumperComplete;
                if (Pack.Healable != null) Pack.Healable.OnHealthChange -= HandleHealthChange;
            }
        }

        private void HandleMovementComplete()
        {
            DespawnInterval(false);
        }

        private void HandleAttackComplete(IHitable target)
        {
            if (target != null && target.EntityType == EntityType.CapacityGate)
            {
                DespawnInterval(true);
                return;
            }

            bool isObstacle = target != null && IsNonEnemyTarget(target.EntityType);

            if (target != null && SoundManager.Instance != null && CanPlayAttackSfxThisFrame())
            {
                var sfx = attackSfx != AudioClipName.None ? attackSfx : AudioClipName.SFX_CharacterAttack;
                if (sfx != AudioClipName.None)
                    SoundManager.Instance.PlayOneShot(sfx);
            }

            if (isObstacle)
            {
                float despawnDelay = ResolveAttackDespawnDelay(obstacleAttackDespawnDelay);
                Pack.Animator?.PlayAnimation(AnimationType.Attack, 0f, null, 1);
                ScheduleAttackDespawn(despawnDelay, playDeathVfxOnAttackDespawn);
                return;
            }

            Pack.Animator?.PlayAnimation(AnimationType.Attack, 0f, null, 1);
            ScheduleAttackDespawn(ResolveAttackDespawnDelay(enemyAttackDespawnDelay), dieVfxPrefab != null);
        }

        private void HandleJumperComplete(IHitable target)
        {
            if (TryAddTarget(target))
            {
                PlayAnimation(AnimationType.Jump);
            }
        }

        protected virtual void HandleHealthChange(int current, int max)
        {
            if (current <= 0)
            {
                DespawnInterval();
            }
        }

        public void PlayAnimation(AnimationType animationType, float waitForAction = 0.5f, Action onComplete = null, int layer = 0)
        {
            if (Pack.Animator != null)
                Pack.Animator.PlayAnimation(animationType, waitForAction, onComplete, layer);
        }

        public void PlayAttackEffect()
        {
            if (SoundManager.Instance != null && CanPlayAttackSfxThisFrame())
            {
                var sfx = attackSfx != AudioClipName.None ? attackSfx : AudioClipName.SFX_CharacterAttack;
                if (sfx != AudioClipName.None)
                    SoundManager.Instance.PlayOneShot(sfx);
            }
        }

        private Transform EnsureProjectilePoint()
        {
            if (projectilePoint != null)
            {
                return projectilePoint;
            }

            GetComponentsInChildren(true, _transformQueryBuffer);
            int projectilePointCount = _transformQueryBuffer.Count;
            for (int i = 0; i < projectilePointCount; i++)
            {
                var candidate = _transformQueryBuffer[i];
                if (candidate == null)
                {
                    continue;
                }

                if (string.Equals(candidate.name, "ProjectilePoint", StringComparison.Ordinal))
                {
                    projectilePoint = candidate;
                    break;
                }
            }
            _transformQueryBuffer.Clear();

            return projectilePoint;
        }

        private Transform EnsureBodyScalable()
        {
            if (bodyscalable != null)
                return bodyscalable;
#if UNITY_EDITOR
            Debug.LogWarning($"[CharacterUnit] BodyScalable missing on {name}. Please assign in Inspector.");
#endif
            return transform;
        }

        private void DespawnInterval(bool playDeathVfx = true)
        {
            RecycleImmediate(playDeathVfx);
        }

        public void RecycleImmediate(bool playDeathVfx = false)
        {
            CancelScheduledDespawn();
            _isAttackDespawnScheduled = false;

            if ((ActiveFlags & CapabilityFlags.Move) != 0) Pack.Mover.Dispose();
            if ((ActiveFlags & CapabilityFlags.Attack) != 0) Pack.Attacker.Dispose();
            if ((ActiveFlags & CapabilityFlags.Jump) != 0) Pack.Jumper.Dispose();
            if ((ActiveFlags & CapabilityFlags.Animator) != 0) Pack.Animator.Dispose();
            if ((ActiveFlags & CapabilityFlags.Heal) != 0) Pack.Healable.Dispose();

            RegisterEvents(false);
            UnregisterProjectileTarget();

            if (_isCountedInRuntime)
            {
                CharacterCount = Mathf.Max(0, CharacterCount - 1);
                _isCountedInRuntime = false;
            }

            ClearHits();
            _isCombatActive = false;

            if (playDeathVfx)
            {
                PlayDeathVfx();
            }

            Despawn();
        }

        public bool IsActive => isActiveAndEnabled;

        public Vector3 Position => Transform.position;

        public Vector3 SelfScale
        {
            get
            {
                var scaleTarget = EnsureBodyScalable();
                return scaleTarget != null ? scaleTarget.localScale : transform.localScale;
            }
        }

        public void OnHit(IAttacker source)
        {
            if (_isAttackDespawnScheduled)
                return;

            // EnemyUnit and BossUnit both resolve their attack through this
            // IHitable path. Make the death-VFX intent explicit rather than
            // relying on DespawnInterval's optional-parameter default.
            RecycleImmediate(playDeathVfx: true);
            OnHitComplete?.Invoke(source);
        }

        private void ScheduleAttackDespawn(float delay, bool playDeathVfx)
        {
            CancelScheduledDespawn();
            float safeDelay = Mathf.Max(0f, delay);
            if (safeDelay <= 0f)
            {
                _isAttackDespawnScheduled = false;
                DespawnInterval(playDeathVfx);
                return;
            }

            _isAttackDespawnScheduled = true;
            s_scheduledDespawns.Add(new ScheduledDespawn
            {
                Unit = this,
                Time = UnityEngine.Time.time + safeDelay,
                PlayDeathVfx = playDeathVfx
            });
        }

        private void CancelScheduledDespawn()
        {
            for (int i = s_scheduledDespawns.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(s_scheduledDespawns[i].Unit, this))
                {
                    continue;
                }

                int last = s_scheduledDespawns.Count - 1;
                if (i != last)
                {
                    s_scheduledDespawns[i] = s_scheduledDespawns[last];
                }

                s_scheduledDespawns.RemoveAt(last);
            }
        }

        public static void TickScheduledDespawns(float currentTime)
        {
            for (int i = s_scheduledDespawns.Count - 1; i >= 0; i--)
            {
                ScheduledDespawn scheduled = s_scheduledDespawns[i];
                if (scheduled.Unit != null && currentTime < scheduled.Time)
                {
                    continue;
                }

                int last = s_scheduledDespawns.Count - 1;
                if (i != last)
                {
                    s_scheduledDespawns[i] = s_scheduledDespawns[last];
                }

                s_scheduledDespawns.RemoveAt(last);
                if (scheduled.Unit == null || !scheduled.Unit.isActiveAndEnabled)
                {
                    continue;
                }

                scheduled.Unit._isAttackDespawnScheduled = false;
                scheduled.Unit.DespawnInterval(scheduled.PlayDeathVfx);
            }
        }

        private void ResetTransientRuntimeState()
        {
            CancelScheduledDespawn();

            _isAttackDespawnScheduled = false;
            RegisterEvents(false);
            UnregisterProjectileTarget();
            ClearHits();
            _isCombatActive = false;
        }



        private float ResolveAttackDespawnDelay(float configuredDelay)
        {
            float delay = Mathf.Max(0f, configuredDelay);
            if (!(Pack.Animator is IAnimationClipLengthProvider clipLengthProvider))
                return delay;

            float attackClipLength = clipLengthProvider.GetAnimationClipLength(AnimationType.Attack);
            if (attackClipLength <= 0f)
                return delay;

            return Mathf.Max(delay, attackClipLength);
        }

        // -----------------------------------------------------------------------
        // [FIX] PlayDeathVfx — use SafePoolGet instead of PoolManager.Instance.Get
        // -----------------------------------------------------------------------

        private void PlayDeathVfx()
        {
            if (dieVfxPrefab == null)
                return;
            if (!CanSpawnDeathVfxThisFrame())
                return;

            // Update last VFX spawn time (start cooldown)
            s_lastDeathVfxTime = Time.time;

            var spawnPos = Transform.position + dieVfxOffset;
            if (!PooledVfxLifetimeScheduler.CanSchedule())
                return;
            var vfx = GetPooledObject(dieVfxPrefab);
            if (vfx == null) return;

            vfx.transform.position = spawnPos;
            vfx.transform.rotation = Quaternion.identity;
            vfx.SetActive(true);

            PooledVfxLifetimeScheduler.Schedule(vfx, dieVfxLifetime);
        }

        public static bool IsBossMassKill = false;
        private const int DeathVfxFrameInterval = 10;

        private bool CanSpawnDeathVfxThisFrame()
        {
            int currentFrame = Time.frameCount;
            if (s_lastDeathVfxFrame >= 0 && currentFrame - s_lastDeathVfxFrame < DeathVfxFrameInterval)
            {
                return false;
            }

            s_lastDeathVfxFrame = currentFrame;
            return true;
        }

        private const int AttackSfxFrameInterval = 20;

        private bool CanPlayAttackSfxThisFrame()
        {
            int currentFrame = Time.frameCount;
            if (s_lastAttackSfxFrame >= 0 && currentFrame - s_lastAttackSfxFrame < AttackSfxFrameInterval)
                return false;

            s_lastAttackSfxFrame = currentFrame;
            return true;
        }

        private bool CanPlayAttackVfxThisFrame()
        {
            int currentFrame = Time.frameCount;
            if (s_lastAttackVfxFrame >= 0 && currentFrame - s_lastAttackVfxFrame < AttackEffectFrameInterval)
                return false;

            s_lastAttackVfxFrame = currentFrame;
            return true;
        }

        private static bool IsNonEnemyTarget(EntityType entityType)
        {
            if (entityType == EntityType.Enemy ||
                entityType == EntityType.Boss ||
                entityType == EntityType.EnemyWeapon ||
                entityType == EntityType.PlayerWeapon ||
                entityType == EntityType.Character ||
                entityType == EntityType.Wheel)
            {
                return false;
            }

            return true;
        }

        public void Dispose()
        {
            // No-op for IHitable/IComponent compatibility
        }

        public override void Free()
        {
            base.Free();
        }

        public ColliderData GetColliderData()
        {
            uint bits = 1u << (int)EntityType;

            if (hitShapeType == ShapeType.Sphere)
            {
                float r = Mathf.Max(0.01f, hitColliderSize.x);
                float centerOffsetY = Mathf.Max(0f, hitColliderSize.y);
                return new ColliderData
                {
                    Type = ShapeType.Sphere,
                    Size = new Vector3(r, centerOffsetY, r),
                    Offset = hitColliderSize.x,
                    CategoryBits = bits
                };
            }

            if (hitShapeType == ShapeType.Cylinder)
            {
                float r = Mathf.Max(0.01f, hitColliderSize.x);
                float halfH = Mathf.Max(0.01f, hitColliderSize.y * 0.5f);
                return new ColliderData
                {
                    Type = ShapeType.Cylinder,
                    Size = new Vector3(r, halfH, r),
                    Offset = hitColliderSize.x,
                    CategoryBits = bits
                };
            }

            Vector3 half = new Vector3(
                Mathf.Max(0.01f, hitColliderSize.x) * 0.5f,
                Mathf.Max(0.01f, hitColliderSize.y) * 0.5f,
                Mathf.Max(0.01f, hitColliderSize.z) * 0.5f);

            return new ColliderData
            {
                Type = ShapeType.Box,
                Size = half,
                Offset = hitColliderSize.z,
                CategoryBits = bits
            };
        }

        private void RegisterProjectileTarget()
        {
            if (_projectileTargetRegistered) return;
            EnemyProjectileSystem.RegisterTarget(this);
            _projectileTargetRegistered = true;
        }

        private void UnregisterProjectileTarget()
        {
            if (!_projectileTargetRegistered) return;
            EnemyProjectileSystem.UnregisterTarget(this);
            _projectileTargetRegistered = false;
        }

        #region JUMP

        private bool TryAddTarget(IHitable target)
        {
            if (target == null) return false;

            for (int i = 0; i < _hitCount; i++)
            {
                if (_hitBuffer[i] == target) return false;
            }

            if (_hitCount < _hitBuffer.Length)
            {
                _hitBuffer[_hitCount] = target;
                _hitCount++;
                return true;
            }

            return false;
        }

        private void ClearHits()
        {
            for (int i = 0; i < _hitCount; i++)
                _hitBuffer[i] = null;

            _hitCount = 0;
        }

        #endregion

        private static Transform FindChildByNameContains(Transform root, string contains)
        {
            if (root == null || string.IsNullOrEmpty(contains)) return null;

            if (root.name.Contains(contains)) return root;

            for (int i = 0; i < root.childCount; i++)
            {
                var match = FindChildByNameContains(root.GetChild(i), contains);
                if (match != null) return match;
            }

            return null;
        }

        private static Transform FindChildRecursive(Transform root, string exactName)
        {
            if (root == null || string.IsNullOrEmpty(exactName)) return null;

            if (root.name == exactName) return root;

            for (int i = 0; i < root.childCount; i++)
            {
                var match = FindChildRecursive(root.GetChild(i), exactName);
                if (match != null) return match;
            }

            return null;
        }
    }
}

