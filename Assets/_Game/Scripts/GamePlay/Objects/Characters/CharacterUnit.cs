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
using DG.Tweening;
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
        [SerializeField] protected Transform[] weaponHolders;
        [HideInInspector] protected Transform weaponHolder;
        [SerializeField] private Transform projectilePoint;
        [SerializeField] private Transform bodyscalable;

        [Header("Effects")]
        [FormerlySerializedAs("effectCaster")]
        [SerializeField] protected EffectComponent effectComponent;

        [Header("Sound Effects")]
        [SerializeField] private AudioClipName attackSfx = AudioClipName.SFX_CharacterAttack;
        [SerializeField, Min(1)] private int maxAttackSfxPerFrame = 6;
        [SerializeField] private bool playAttackVfxOnObstacleHit = false;
        [SerializeField, Min(1)] private int maxAttackVfxPerFrame = 3;
        [SerializeField, Min(0f)] private float obstacleAttackDespawnDelay = 0.5f;
        [SerializeField, Min(0f)] private float enemyAttackDespawnDelay = 0.5f;

        [Header("Hit Settings")]
        [SerializeField] private ShapeType hitShapeType = ShapeType.Cylinder;
        [SerializeField] private Vector3 hitColliderSize = new Vector3(0.6f, 1.2f, 0.6f);

        [SerializeField] public int Level = -1;

        private GameObject _currentWeapon;
        private WeaponUnit _currentWeaponUnit;
        private Renderer[] _currentWeaponRenderers;
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
        [SerializeField] private int maxDeathVfxPerFrame = 20;
        [SerializeField] private bool playDeathVfxOnAttackDespawn = false;
        private static float s_lastDeathVfxTime = -999f;
        private static int s_lastDeathVfxFrame = -1;
        private static int s_deathVfxCountInFrame = 0;
        private static int s_lastAttackSfxFrame = -1;
        private static int s_attackSfxCountInFrame = 0;
        private static int s_lastAttackVfxFrame = -1;
        private const int AttackEffectFrameInterval = 10;
        private bool _isAttackDespawnScheduled;

        public Transform ProjectilePoint => EnsureProjectilePoint();

        private static GameObject SafePoolGet(GameObject prefab)
        {
            if (prefab == null) return null;

            try
            {
                var obj = prefab.Spawn();
                return obj != null ? obj : Instantiate(prefab);
            }
            catch
            {
                try
                {
                    return Instantiate(prefab);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CharacterUnit] SafePoolGet failed for prefab: {e.Message}");
                    return null;
                }
            }
        }

        private static void SafePoolRelease(GameObject obj)
        {
            if (obj == null) return;

            try
            {
                obj.Despawn();
            }
            catch
            {
                Destroy(obj);
            }
        }

        private string _originalWeaponHolderName;

        protected override void Awake()
        {
            base.Awake();

            BuildCapabilityPack();
            Level = -1;

            if (weaponHolder != null)
            {
                _originalWeaponHolderName = weaponHolder.name;
            }

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
            Setup(level, includeWeapon: true);
            ShowWeapon();

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
            Setup(level, includeWeapon: false);

            if ((ActiveFlags & CapabilityFlags.Animator) != 0)
            {
                Pack.Animator.Initialize();
                Pack.Animator.PlayAnimation(AnimationType.Idle, 0f, null, 0);
            }

            HideWeapon();
        }

        private void OnDisable()
        {
            ReleaseCurrentWeapon();

            DOTween.Kill(this, "AttackDespawn");
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

            Transform oldHolder = weaponHolder;

            for (int i = 0; i < visualModels.Length; i++)
            {
                if (visualModels[i] != null)
                {
                    bool isActive = (i == safeIndex);
                    visualModels[i].SetActive(isActive);

                    if (isActive)
                    {

                        if (Pack.Animator != null && Pack.Animator is AnimationComponent animComp)
                        {
                            animComp.SetAnimatorLevel(i);
                        }

                        if (weaponHolders != null && i >= 0 && i < weaponHolders.Length && weaponHolders[i] != null)
                        {
                            weaponHolder = weaponHolders[i];

                            if (_currentWeapon != null)
                            {
                                _currentWeapon.transform.SetParent(weaponHolder, false);
                                _currentWeapon.transform.localPosition = Vector3.zero;
                                _currentWeapon.transform.localRotation = Quaternion.identity;
                                _currentWeapon.transform.localScale = Vector3.one;
                            }
                        }
                    }
                }
            }

            if (_currentWeaponUnit != null)
            {
                _currentWeaponUnit.ApplyVisualLevel(levelIndex);
            }
        }

        public void Setup(int level, bool includeWeapon = true)
        {
            Level = level;
            SetupComponents();
            if (includeWeapon && _currentWeapon == null)
            {
                // Weapon is usually handled by PlayerArmySystem now
            }
        }

        private void SetupComponents()
        {
            if ((ActiveFlags & CapabilityFlags.Attack) != 0)
                Pack.Attacker.Setup(1); // Base damage, modified by ArmyUpgradeManager
        }

        private void SetupModel(bool includeWeapon = true)
        {
            if (!includeWeapon)
            {
                ReleaseCurrentWeapon();
                return;
            }

            SetupWeapon();
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
                if (playAttackVfxOnObstacleHit && effectComponent != null && CanPlayAttackVfxThisFrame())
                    effectComponent.PlayEffect(EffectType.Attack);

                float despawnDelay = ResolveAttackDespawnDelay(obstacleAttackDespawnDelay);
                Pack.Animator?.PlayAnimation(AnimationType.Attack, 0f, null, 1);
                ScheduleAttackDespawn(despawnDelay, playDeathVfxOnAttackDespawn);
                return;
            }

            if (effectComponent != null && CanPlayAttackVfxThisFrame())
                effectComponent.PlayEffect(EffectType.Attack);

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
            if (effectComponent != null && CanPlayAttackVfxThisFrame())
                effectComponent.PlayEffect(EffectType.Attack, transform.position, transform.rotation);
        }


        // -----------------------------------------------------------------------
        // [FIX] SetupWeapon — use SafePoolGet instead of PoolManager.Instance.Get
        // -----------------------------------------------------------------------
        private void SetupWeapon()
        {
            // Now managed by SetWeaponPrefabOverride
        }

        public void HideWeapon()
        {
            SetWeaponVisible(false);
        }

        public void ShowWeapon()
        {
            if (_currentWeapon == null)
            {
                SetupWeapon();
            }

            SetWeaponVisible(true);
        }

        public Transform ActiveWeaponHolder
        {
            get
            {
                if (weaponHolder != null) return weaponHolder;
                if (weaponHolders == null || weaponHolders.Length == 0) return null;
                int idx = Mathf.Clamp(Level >= 0 ? Level : 0, 0, weaponHolders.Length - 1);
                return weaponHolders[idx];
            }
        }

        public void SetWeaponPrefabOverride(GameObject prefab)
        {
            var holder = ActiveWeaponHolder;
            if (holder != null)
            {
                for (int i = holder.childCount - 1; i >= 0; i--)
                {
                    var child = holder.GetChild(i);
                    SafePoolRelease(child.gameObject);
                }
            }

            _currentWeapon = null;
            _currentWeaponRenderers = null;

            if (prefab == null)
            {
                return;
            }

            _currentWeapon = SafePoolGet(prefab);

            if (_currentWeapon == null)
            {
                return;
            }

            var weaponTransform = _currentWeapon.transform;
            if (holder != null)
            {
                weaponTransform.SetParent(holder, false);
            }
            weaponTransform.localPosition = Vector3.zero;
            weaponTransform.localRotation = Quaternion.identity;
            weaponTransform.localScale = Vector3.one;

            _currentWeaponRenderers = _currentWeapon.GetComponentsInChildren<Renderer>(true);
            if (_currentWeapon.TryGetComponent<WeaponUnit>(out var weaponUnit))
            {
                weaponUnit.SetDefault();
            }
        }

        private Transform EnsureProjectilePoint()
        {
            if (projectilePoint != null)
            {
                return projectilePoint;
            }

            var projectilePoints = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < projectilePoints.Length; i++)
            {
                var candidate = projectilePoints[i];
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

        public WeaponUnit DetachWeaponForProjectile()
        {
            if (_currentWeapon == null)
            {
                return null;
            }

            var weapon = _currentWeapon;
            var weaponUnit = weapon.GetComponent<WeaponUnit>();
            if (weaponUnit == null)
            {
                weaponUnit = weapon.GetComponentInChildren<WeaponUnit>(true);
            }
            if (weaponUnit == null)
            {
                return null;
            }

            _currentWeapon = null;
            _currentWeaponRenderers = null;
            weapon.transform.SetParent(null, true);
            return weaponUnit;
        }

        private void DespawnInterval(bool playDeathVfx = true)
        {
            RecycleImmediate(playDeathVfx);
        }

        public void RecycleImmediate(bool playDeathVfx = false)
        {
            DOTween.Kill(this, "AttackDespawn");
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

            if (effectComponent != null)
            {
                effectComponent.PlayEffect(EffectType.Hit, transform.position, transform.rotation);
            }

            DespawnInterval();
            OnHitComplete?.Invoke(source);
        }

        private void ScheduleAttackDespawn(float delay, bool playDeathVfx)
        {
            DOTween.Kill(this, "AttackDespawn");
            float safeDelay = Mathf.Max(0f, delay);
            if (safeDelay <= 0f)
            {
                _isAttackDespawnScheduled = false;
                DespawnInterval(playDeathVfx);
                return;
            }

            _isAttackDespawnScheduled = true;
            DOVirtual.DelayedCall(safeDelay, () =>
            {
                _isAttackDespawnScheduled = false;
                DespawnInterval(playDeathVfx);
            }, false).SetId(this).SetId("AttackDespawn");
        }

        private void ResetTransientRuntimeState()
        {
            DOTween.Kill(this, "AttackDespawn");

            _isAttackDespawnScheduled = false;
            RegisterEvents(false);
            UnregisterProjectileTarget();
            ClearHits();
            _isCombatActive = false;
        }



        private float ResolveAttackDespawnDelay(float configuredDelay)
        {
            float delay = Mathf.Max(0f, configuredDelay);
            if (!(Pack.Animator is AnimationComponent animationComponent))
                return delay;

            float attackClipLength = animationComponent.GetAnimationClipLength(AnimationType.Attack);
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
            var vfx = SafePoolGet(dieVfxPrefab);
            if (vfx == null) return;

            vfx.transform.position = spawnPos;
            vfx.transform.rotation = Quaternion.identity;
            vfx.SetActive(true);

            DOVirtual.DelayedCall(Mathf.Max(0.05f, dieVfxLifetime), () =>
            {
                if (vfx != null) SafePoolRelease(vfx);
            }, false).SetId(vfx);
        }



        public static bool IsBossMassKill = false;

        private bool CanSpawnDeathVfxThisFrame()
        {
            if (Time.frameCount != s_lastDeathVfxFrame)
            {
                s_lastDeathVfxFrame = Time.frameCount;
                s_deathVfxCountInFrame = 0;
            }

            int limit = IsBossMassKill ? 10 : 1;

            if (s_deathVfxCountInFrame >= limit)
                return false;

            s_deathVfxCountInFrame++;
            return true;
        }

        private bool CanPlayAttackSfxThisFrame()
        {
            if (Time.frameCount != s_lastAttackSfxFrame)
            {
                s_lastAttackSfxFrame = Time.frameCount;
                s_attackSfxCountInFrame = 0;
            }

            int cap = Mathf.Max(1, maxAttackSfxPerFrame);
            if (s_attackSfxCountInFrame >= cap)
                return false;

            s_attackSfxCountInFrame++;
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
            ReleaseCurrentWeapon();
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

        private void ReleaseCurrentWeapon()
        {
            if (_currentWeapon == null) return;

            _currentWeapon.transform.SetParent(null, false);
            SafePoolRelease(_currentWeapon);

            _currentWeapon = null;
            _currentWeaponRenderers = null;
        }

        private void SetWeaponVisible(bool visible)
        {
            if (_currentWeapon == null) return;

            if (_currentWeaponRenderers == null || _currentWeaponRenderers.Length == 0)
                _currentWeaponRenderers = _currentWeapon.GetComponentsInChildren<Renderer>(true);

            for (int i = 0; i < _currentWeaponRenderers.Length; i++)
            {
                var renderer = _currentWeaponRenderers[i];
                if (renderer == null) continue;
                renderer.enabled = visible;
            }
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

