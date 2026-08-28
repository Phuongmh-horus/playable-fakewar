using System;
using System.Collections;
using System.Collections.Generic;
using GamePlay.AnimationSystems;
using GamePlay.Characters;
using GamePlay.CollisionSystems;
using GamePlay.CombatSystems;
using GamePlay.ComponentSystems;
using GamePlay.Crushers;
using GamePlay.Entities;
using GamePlay.Inputs;
using GamePlay.Weapons;
using Pools;
using UnityEngine;
using DG.Tweening;

namespace PlayerArmy
{
    public enum PlayerArmyState : byte { Idle, IntroRun, Active }
    public enum PlayerArmyAttackMode : byte { Melee, ForwardRanged, ThrownProjectile }

    [DisallowMultipleComponent]
    public class PlayerArmySystem : MonoBehaviour, IAttacker
    {
        [Header("Movement")]
        [SerializeField] private Transform bodyRoot;
        [SerializeField] private float fallbackForwardSpeed = 6f;
        [SerializeField] private float fallbackSpeedChangeRate = 2f;
        [SerializeField, Min(0f)] private float inputSensitivity = 0.015f;
        [SerializeField, Min(1f)] private float strafeFollowMultiplier = 2f;
        [SerializeField] private float xLimit = 4f;
        [SerializeField] private float collisionCheckRangeX = 7f;
        [SerializeField] private float collisionCheckRangeZ = 25f;
        [SerializeField] private Vector2 collisionSize = new Vector2(3f, 3f);

        private const float LateralAnimationThreshold = 0.05f;
        private AnimationType _currentMovementAnimation = AnimationType.None;

        [Header("Spawn")]
        [SerializeField] private CharacterUnit characterPrefab;
        [SerializeField] private WeaponUnit weaponProjectilePrefab;

        public CharacterUnit CharacterPrefab => characterPrefab;
        [SerializeField, Min(1)] private int fallbackCharacterLevel = 1;
        [SerializeField, Min(1)] private int maxActiveSpawnedUnits = 51;
        [SerializeField, Min(0f)] private float unitSpacing = 1.2f;
        [SerializeField, Tooltip("Dùng trực tiếp các character đã đặt sẵn trên scene để giảm thời gian spawn lúc boot.")] private bool useSceneUnitsOnly = true;
        [SerializeField, Min(0), Tooltip("Số character inactive tối thiểu chuẩn bị trước cho FireSoldier +1 và SoldierBall.")] private int characterPrewarmReserve = 30;

        [Header("Attack")]
        [SerializeField] private PlayerArmyAttackMode attackMode = PlayerArmyAttackMode.ThrownProjectile;
        [SerializeField, Min(0.1f)] private Vector2 meleeAttackSize = new Vector2(1.4f, 2.2f);
        [SerializeField, Min(0.1f)] private Vector2 rangedAttackSize = new Vector2(1.8f, 6f);
        [SerializeField, Min(0f)] private float attackOriginOffset = 0.9f;
        [Header("Damage Settings")]
        [SerializeField, Min(1)] private int _baseAttackDamage = 5;
        [SerializeField, Min(1)] private int damageBonusPerUpgrade = 5;
        [SerializeField] private int _baseProjectileDamage = 5;
        private int attackDamage = 5;

        [Header("Projectile")]
        [SerializeField, Min(0.05f)] private float attackInterval = 0.75f;
        [SerializeField, Min(0.1f)] private float projectileDistance = 6f;
        [SerializeField, Min(0.05f)] private float projectileDuration = 0.55f;
        [SerializeField] private float projectileRotationSpeed = 540f;
        [SerializeField, Min(1)] private int maxProjectileLaunchesPerFrame = 10;
        [SerializeField, Tooltip("Maximum army units evaluated for an attack per tick.")]
        private int maxAttackEvaluationsPerTick = 24;
        [SerializeField, Min(0f)] private float unitscalevalue = 1.35f;

        [Header("Refs")]
        [SerializeField] private InputManager inputManager;
        [SerializeField] private PlayerArmyEffectSystem effectSystem;

        [Header("Runtime Tick")]
        [SerializeField, Min(1)] private int collisionTickInterval = 2;
        [SerializeField, Min(1)] private int attackTickInterval = 2;
        [SerializeField, Min(1)] private int pruneTickInterval = 15;

        [Header("Runtime Units")]
        [SerializeField] private List<CharacterUnit> characterUnits = new List<CharacterUnit>();

        public event Action<IHitable> OnAttackComplete;

        private PlayerArmyState currentState = PlayerArmyState.Active;
        private float _currentForwardSpeed;
        private float _targetX;
        private int _tickOffset;

        private HashSet<int> _currentEnemyContactIds = new HashSet<int>();
        private HashSet<int> _previousEnemyContactIds = new HashSet<int>();
        private HashSet<int> _currentEnvironmentContactIds = new HashSet<int>();
        private HashSet<int> _previousEnvironmentContactIds = new HashSet<int>();
        private struct PendingProjectileAttack
        {
            public CharacterUnit Unit;
            public float TriggerTime;
        }
        private readonly List<PendingProjectileAttack> _pendingProjectileAttacks = new List<PendingProjectileAttack>(80);
        private readonly List<CharacterUnit> _unitSnapshotBuffer = new List<CharacterUnit>(64);
        private readonly Dictionary<int, GamePlay.Items.StatModifierGate> _fireSoldierGateCache = new Dictionary<int, GamePlay.Items.StatModifierGate>(16);
        private readonly List<int> _collisionQueryIndices = new List<int>(64);
        private readonly bool[] _occupiedArmyIndices = new bool[HardMaxActiveSpawnedUnits];
        private int _resolvedWeaponDamage;
        private int _damageBonusPoints;
        private float _baseAttackInterval;
        private float _baseProjectileDuration;
        private int _fireRateBonusPoints;
        private float _baseFireRange;
        private float _fireRangeBonus;
        private bool _wasProjectileFireSuppressed;
        private float _projectileFireResumeTime = float.NegativeInfinity;
        private static readonly Dictionary<int, Vector2Int[]> s_honeycombRingCache = new Dictionary<int, Vector2Int[]>(16);

        private readonly List<IHitable> _frameAttackableTargets = new List<IHitable>(32);
        private readonly List<Transform> _frameAttackableTransforms = new List<Transform>(32);
        private readonly List<float> _frameAttackableHalfWidths = new List<float>(32);

        private const int HardMaxActiveSpawnedUnits = 51;
        private const float HoneycombForwardStepFactor = 0.8660254f;
        private static readonly Vector2Int[] HoneycombDirections = new Vector2Int[6]
        {
            new Vector2Int(1, 0), new Vector2Int(0, 1), new Vector2Int(-1, 1),
            new Vector2Int(-1, 0), new Vector2Int(0, -1), new Vector2Int(1, -1)
        };

        private readonly HashSet<int> _finishTowerHitIdsThisFrame = new HashSet<int>();
        private readonly List<CharacterUnit> _finishTowerHitUnitsBuffer = new List<CharacterUnit>(32);
        private int _finishTowerLastHitFrame = -1;
        private const int MaxVisualUpgradesPerFrame = 5;
        private int _pendingVisualUpgradeLevel = -1;
        private int _pendingVisualUpgradeIndex;

        private readonly Dictionary<string, int> _samuraiAttackCounters = new Dictionary<string, int>();
        private float _lastSwordSkillIncrementTime = -1f;
        private int _lastLaunchFrame = -1;
        private int _currentAttackEvalIndex = 0;

        private int _pendingSpawnAmount = 0;
        private int _pendingSpawnLevel = -1;
        private bool _pendingSpawnPlayAnimation = false;
        private const int MaxSpawnsPerFrame = 5;

        public IReadOnlyList<CharacterUnit> Units => characterUnits;
        public PlayerArmyEffectSystem EffectSystem => effectSystem;
        public PlayerArmyState CurrentState => currentState;
        public int ResolvedWeaponDamage => _resolvedWeaponDamage;
        public bool IsActive => currentState != PlayerArmyState.Idle;
        public Transform BodyTransform => bodyRoot != null ? bodyRoot : transform;

        public Transform Transform => transform;
        public bool IsEnabled => isActiveAndEnabled;
        public Vector3 Position => bodyRoot != null ? bodyRoot.position : transform.position;
        public EntityType EntityType => EntityType.Wheel;
        public Vector2 Size => collisionSize;
        public int Damage => ResolveEffectiveAttackDamage();
        public uint TargetMask => 1 << (int)EntityType.Item |
                                         1 << (int)EntityType.Enemy |
                                         1 << (int)EntityType.Boss |
                                         1 << (int)EntityType.Obstacle |
                                         1 << (int)EntityType.MovingGate |
                                         1 << (int)EntityType.ResourceTower |
                                         1 << (int)EntityType.CapacityFactory |
                                         1 << (int)EntityType.CapacityGate |
                                         1 << (int)EntityType.PowerGate |
                                         1 << (int)EntityType.FinishTrigger |
                                         1 << (int)EntityType.FinishTower |
                                         1 << (int)EntityType.GateNewEra;


        private bool _isInitialized = false;

        public void Initialize()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            ResolveDependencies();
            CacheDefaultState();
            //ClearSceneUnits();
            ResetRuntimeSpawnState();
            _tickOffset = Mathf.Abs(GetInstanceID()) % Mathf.Max(1, pruneTickInterval);

            var sceneUnits = GetComponentsInChildren<CharacterUnit>(true);
            characterUnits.Clear();
            for (int i = 0; i < sceneUnits.Length; i++)
            {
                var unit = sceneUnits[i];
                if (unit == null || unit == this) continue;

                if (characterUnits.Count >= HardMaxActiveSpawnedUnits)
                {
                    unit.gameObject.SetActive(false);
                    continue;
                }

                AddUnit(unit, true, true);
            }
        }

        private void Start()
        {
            if (!_isInitialized)
            {
                Initialize();
                currentState = PlayerArmyState.Active;
            }

            GameEventBus.OnAddWheelCard += HandleAddArmyCardEvent;
        }

        public IEnumerator PrewarmArmyPrefabsAsync(int maxPerFrame)
        {
            int batchSize = Mathf.Max(1, maxPerFrame);
            if (characterPrefab != null && !characterPrefab.gameObject.scene.IsValid())
            {
                int activeSceneUnits = CountActiveSceneUnits();
                int runtimeCapacity = Mathf.Max(0, Mathf.Clamp(maxActiveSpawnedUnits, 1, HardMaxActiveSpawnedUnits) - activeSceneUnits);
                int requiredInactiveCount = Mathf.Min(runtimeCapacity, Mathf.Max(0, characterPrewarmReserve));
                yield return PoolSystem.EnsurePrewarmAsync(characterPrefab, requiredInactiveCount, batchSize);

                var characterUnit = characterPrefab.GetComponent<CharacterUnit>();
                if (characterUnit != null && characterUnit.DieVfxPrefab != null)
                {
                    var dieVfxTransform = characterUnit.DieVfxPrefab.transform;
                    if (dieVfxTransform != null && !characterUnit.DieVfxPrefab.scene.IsValid())
                    {
                        yield return PoolSystem.EnsurePrewarmAsync(dieVfxTransform, 10, batchSize);
                    }
                }
            }

            if (weaponProjectilePrefab != null && !weaponProjectilePrefab.gameObject.scene.IsValid())
            {
                int projectileReserve = Mathf.Clamp(
                    Mathf.Max(maxProjectileLaunchesPerFrame * 5, maxAttackEvaluationsPerTick * 2),
                    20,
                    128);
                yield return PoolSystem.EnsurePrewarmAsync(weaponProjectilePrefab, projectileReserve, batchSize);
            }
        }

        private int CountActiveSceneUnits()
        {
            int count = 0;
            for (int i = 0; i < characterUnits.Count; i++)
            {
                var unit = characterUnits[i];
                if (unit != null && unit.IsActive && unit.gameObject.scene.IsValid())
                {
                    count++;
                }
            }

            return count;
        }

        private void OnDestroy()
        {
            GameEventBus.OnAddWheelCard -= HandleAddArmyCardEvent;
        }

        private void HandleAddArmyCardEvent()
        {
            if (useSceneUnitsOnly && characterUnits.Count > 0)
            {
                return;
            }

            // SpawnUnits(fallbackCharacterLevel, 1);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            ResolveDependencies();
            maxActiveSpawnedUnits = Mathf.Clamp(maxActiveSpawnedUnits, 1, HardMaxActiveSpawnedUnits);
            if (characterUnits == null) characterUnits = new List<CharacterUnit>();
        }
#endif

        public void ManualUpdate()
        {
            ProcessPendingSpawns();
            ProcessPendingVisualUpgrades();

            if (_pendingSwordSkills.Count > 0)
            {
                for (int i = _pendingSwordSkills.Count - 1; i >= 0; i--)
                {
                    var skill = _pendingSwordSkills[i];
                    skill.RemainingDelay -= Time.deltaTime;
                    if (skill.RemainingDelay <= 0f)
                    {
                        LaunchSwordSkill(skill.WeaponPrefab, skill.SamuraiConfig, skill.Unit, skill.StartPoint, skill.Forward, skill.Rotation, skill.Distance, skill.Damage);
                        _pendingSwordSkills.RemoveAt(i);
                    }
                    else
                    {
                        _pendingSwordSkills[i] = skill;
                    }
                }
            }

            if (currentState == PlayerArmyState.Idle || !GameplayManager.IsGameStarted)
            {
                return;
            }

            float dt = Time.deltaTime;

            UpdateMovement(dt);

            if (currentState == PlayerArmyState.Active)
            {
                int frame = Time.frameCount + _tickOffset;
                if (frame % Mathf.Max(1, pruneTickInterval) == 0)
                {
                    PruneInactiveSpawnedUnits();
                }

                if (frame % Mathf.Max(1, collisionTickInterval) == 0)
                {
                    UpdateCollisionChecks();
                }

                if (frame % Mathf.Max(1, attackTickInterval) == 0)
                {
                    UpdateCharacterAttacks();
                }

                UpdatePendingProjectileAttacks();
            }
            else
            {
                ClearContactState();
            }
        }

        public void AddUnit(CharacterUnit unit, bool parentToRoot = true)
        {
            AddUnit(unit, parentToRoot, true);
        }

        public void AddUnit(CharacterUnit unit, bool parentToRoot, bool initialize)
        {
            if (unit == null || characterUnits.Contains(unit)) return;
            if (CountActiveUnits() >= Mathf.Clamp(maxActiveSpawnedUnits, 1, HardMaxActiveSpawnedUnits))
            {
                unit.RecycleImmediate(false);
                return;
            }

            unit.ArmyIndex = GetNextAvailableArmyIndex();
            characterUnits.Add(unit);
            if (parentToRoot) unit.transform.SetParent(GetBodyRoot(), true);

            if (initialize && !useSceneUnitsOnly)
            {
                InitializeRuntimeUnit(unit, ResolveSpawnLevel(unit.Level > 0 ? unit.Level : fallbackCharacterLevel));
            }
            else if (!useSceneUnitsOnly)
            {
                RegisterRuntimeUnit(unit);
                SetNextAttackTime(unit, Time.time + attackInterval, true);
            }
            else
            {
                SetNextAttackTime(unit, Time.time + attackInterval, true);
            }
        }

        public bool RemoveUnit(CharacterUnit unit, bool deactivate = false)
        {
            if (unit == null || !characterUnits.Contains(unit)) return false;
            characterUnits.Remove(unit);
            UnregisterRuntimeUnit(unit, deactivate);
            TryTriggerLoseWhenArmyEmpty();
            return true;
        }

        public void ClearUnits(bool deactivate = false)
        {
            for (int i = characterUnits.Count - 1; i >= 0; i--)
            {
                UnregisterRuntimeUnit(characterUnits[i], deactivate);
            }

            characterUnits.Clear();
        }

        public CharacterUnit SpawnCharacterUnit(int level, Vector3 position, Quaternion rotation, float? nextAttackTime = null, bool playMoveAnimation = false)
        {
            if (!TryReserveSpawnSlot())
            {
                return null;
            }

            int index = GetNextAvailableArmyIndex();
            if (index < 0 || index >= HardMaxActiveSpawnedUnits)
            {
                return null;
            }

            var unit = CreateRuntimeCharacterUnit(level, position, rotation, nextAttackTime, playMoveAnimation);
            if (unit == null)
            {
                return null;
            }

            if (!characterUnits.Contains(unit))
            {
                characterUnits.Add(unit);
            }

            unit.ArmyIndex = index;
            unit.transform.SetPositionAndRotation(position, rotation);

            return unit;
        }

        private CharacterUnit CreateRuntimeCharacterUnit(int level, Vector3 position, Quaternion rotation, float? nextAttackTime = null, bool playMoveAnimation = false, bool refreshCombatProfile = true)
        {
            int resolvedLevel = ResolveSpawnLevel(level);

            if (characterPrefab == null)
            {
                return null;
            }

            var unit = characterPrefab.Spawn(position, rotation, GetBodyRoot());
            if (unit == null)
            {
                return null;
            }

            InitializeRuntimeUnit(unit, resolvedLevel, nextAttackTime, playMoveAnimation, refreshCombatProfile);
            return unit;
        }

        private int ResolveSpawnLevel(int requestedLevel)
        {
            return Mathf.Max(1, requestedLevel > 0 ? requestedLevel : fallbackCharacterLevel);
        }

        private struct DelayedSwordSkill
        {
            public WeaponUnit WeaponPrefab;
            public CardSystem.Data.SamuraiSkillConfigSO SamuraiConfig;
            public CharacterUnit Unit;
            public Vector3 StartPoint;
            public Vector3 Forward;
            public Quaternion Rotation;
            public float Distance;
            public int Damage;
            public float RemainingDelay;
        }

        private readonly List<DelayedSwordSkill> _pendingSwordSkills = new List<DelayedSwordSkill>(8);

        private void LaunchSwordSkill(
            WeaponUnit weaponPrefab,
            CardSystem.Data.SamuraiSkillConfigSO samuraiConfig,
            CharacterUnit unit,
            Vector3 startPoint,
            Vector3 forward,
            Quaternion rotation,
            float distance,
            int damage)
        {
            if (this == null || !IsActive || unit == null || weaponPrefab == null || samuraiConfig == null) return;

            float speed = samuraiConfig.ProjectileSpeed > 0 ? samuraiConfig.ProjectileSpeed : 60f;
            float duration = distance / speed;
            var swordProjectile = weaponPrefab.Spawn(startPoint, rotation, null);
            if (swordProjectile != null)
            {
                if (ArmyUpgradeManager.Instance != null)
                {
                    swordProjectile.ApplyVisualLevel(ArmyUpgradeManager.Instance.CurrentLevel);
                }
                swordProjectile.transform.localScale = unit.SelfScale * Mathf.Max(0f, unitscalevalue);
                swordProjectile.SetFly();
                if (!swordProjectile.Launch(
                    startPoint,
                    forward,
                    distance,
                    duration,
                    0f,
                    0f,
                    damage,
                    EnemyProjectileSystem.ProjectileSpinAxis.None,
                    EnemyProjectileSystem.ProjectileMotionMode.Straight,
                    null,
                    false))
                {
                    swordProjectile.Despawn();
                }
            }
        }

        private void SpawnUnits(int level, int amount, float? nextAttackTime = null, bool playMoveAnimation = false)
        {
            int spawnCount = Mathf.Max(0, amount);
            if (spawnCount <= 0)
            {
                return;
            }

            _pendingSpawnAmount += spawnCount;
            _pendingSpawnLevel = level;
            _pendingSpawnPlayAnimation = playMoveAnimation;
        }

        private void ProcessPendingSpawns()
        {
            if (_pendingSpawnAmount <= 0)
            {
                return;
            }

            int spawnCount = Mathf.Min(_pendingSpawnAmount, MaxSpawnsPerFrame);
            _pendingSpawnAmount -= spawnCount;
            int level = _pendingSpawnLevel;
            bool playMoveAnimation = _pendingSpawnPlayAnimation;
            float? nextAttackTime = null;

            PruneInactiveSpawnedUnits();
            int activeUnitCap = Mathf.Clamp(maxActiveSpawnedUnits, 1, HardMaxActiveSpawnedUnits);
            int availableSlots = activeUnitCap - characterUnits.Count;
            if (availableSlots <= 0)
            {
                return;
            }

            spawnCount = Mathf.Min(spawnCount, availableSlots);
            int resolvedLevel = ResolveSpawnLevel(level);
            Transform root = GetBodyRoot();
            Quaternion rotation = root.rotation;

            int totalCount = activeUnitCap;
            BuildOccupiedArmyIndices();
            ApplyUnitCombatProfile();

            for (int i = 0; i < spawnCount; i++)
            {
                int index = FindNextAvailableArmyIndex();
                if (index < 0)
                {
                    break;
                }

                _occupiedArmyIndices[index] = true;
                Vector3 spawnPosition = GetHoneycombSpawnPosition(root, index, totalCount);
                var unit = CreateRuntimeCharacterUnit(resolvedLevel, spawnPosition, rotation, nextAttackTime, playMoveAnimation, false);
                if (unit != null)
                {
                    if (!characterUnits.Contains(unit))
                    {
                        characterUnits.Add(unit);
                    }

                    unit.ArmyIndex = index;
                    unit.transform.SetPositionAndRotation(spawnPosition, rotation);
                }
            }

        }

        public void PlayEffect(EffectType effectType, Transform anchor = null, Action onComplete = null, float waitForAction = 0f)
        {
            effectSystem?.PlayEffect(effectType, anchor != null ? anchor : GetBodyRoot(), onComplete, waitForAction);
        }

        public void PlayEffectAt(EffectType effectType, Vector3 position, Quaternion rotation, Transform parent = null, Action onComplete = null, float waitForAction = 0f)
        {
            effectSystem?.PlayEffectAt(effectType, position, rotation, parent != null ? parent : GetBodyRoot(), onComplete, waitForAction);
        }

        public void OnAttackSucceed(IHitable target)
        {
            OnAttackComplete?.Invoke(target);
        }

        public void Setup(int damage)
        {
            _baseAttackDamage = Mathf.Max(1, damage);
            RefreshCombatDamage();
        }

        public void ApplyFireRangeModifier(int value)
        {
            if (value == 0)
            {
                return;
            }

            _fireRangeBonus += value;
            RefreshFireRange();
        }

        private void ApplyUnitCombatProfile()
        {
            // _baseFireRange is kept unchanged since weapon visual config is removed.
            // Consider moving _baseFireRange configuration to the Inspector.
            RefreshCombatDamage();
            RefreshFireRange();
        }

        private void RefreshCombatDamage()
        {
            attackDamage = Mathf.Max(1, _baseAttackDamage + Mathf.Max(0, _resolvedWeaponDamage) + Mathf.Max(0, _damageBonusPoints));
        }

        private void RefreshFireRange()
        {
            projectileDistance = Mathf.Max(0.1f, _baseFireRange + _fireRangeBonus);
        }

        private int ResolveEffectiveAttackDamage()
        {
            return Mathf.Max(1, attackDamage);
        }

        public void Dispose()
        {
            ClearUnits(true);
        }

        public void SetIdle()
        {
            currentState = PlayerArmyState.Idle;
            _currentMovementAnimation = AnimationType.Idle;
            ClearPendingProjectileAttacks();
            ClearContactState();
            foreach (var unit in characterUnits)
            {
                if (unit != null && unit.IsActive)
                {
                    unit.PlayAnimation(AnimationType.Idle, 0f, null, 0);
                }
            }
        }

        public void SetActive()
        {
            currentState = PlayerArmyState.Active;
            _currentMovementAnimation = AnimationType.Attack;
            ClearPendingProjectileAttacks();
            ClearContactState();
            if (characterUnits != null)
            {
                for (int i = 0; i < characterUnits.Count; i++)
                {
                    if (characterUnits[i] != null)
                    {
                        characterUnits[i].PlayAnimation(AnimationType.Attack, 0f, null, 0);
                        SetNextAttackTime(characterUnits[i], Time.time, true);
                    }
                }
            }
        }

        private void ClearPendingProjectileAttacks()
        {
            _pendingProjectileAttacks.Clear();
        }

        private void ClearContactState()
        {
            _previousEnemyContactIds.Clear();
            _currentEnemyContactIds.Clear();
            _previousEnvironmentContactIds.Clear();
            _currentEnvironmentContactIds.Clear();
        }

        public void AddCards(List<CardSpawnRequestData> requests, CardSpawnEffectType effectType)
        {
            if (useSceneUnitsOnly && effectType == CardSpawnEffectType.DropWithoutAction && characterUnits.Count > 0)
            {
                int targetLevel = fallbackCharacterLevel;
                if (requests != null && requests.Count > 0 && requests[0].Level > 0)
                {
                    targetLevel = requests[0].Level;
                }

                ApplyUnitCombatProfile();

                for (int i = 0; i < characterUnits.Count; i++)
                {
                    var unit = characterUnits[i];
                    if (unit == null) continue;

                    unit.gameObject.SetActive(true);
                    unit.Initialize(targetLevel, true);
                    unit.Setup(targetLevel);
                    unit.PlayAnimation(ResolveRuntimeUnitAnimation(), 0f, null, 0);
                }
                return;
            }

            bool playMoveAnimation = effectType != CardSpawnEffectType.DropWithoutAction;

            for (int i = 0; i < requests.Count; i++)
            {
                var req = requests[i];
                int level = req.Level > 0 ? req.Level : ResolveCurrentArmyLevel();
                int amount = Mathf.Max(1, req.Amount);
                SpawnUnits(level, amount, null, playMoveAnimation);
            }
        }

        public void ApplyFireRateModifier(int value)
        {
            if (value <= 0)
            {
                return;
            }

            _fireRateBonusPoints += value;

            // [FIX] Use multiplier logic to scale down interval without hitting the floor too fast
            float multiplier = 1f / (1f + _fireRateBonusPoints * 0.005f);
            attackInterval = Mathf.Max(0.01f, _baseAttackInterval * multiplier);
            projectileDuration = Mathf.Max(0.01f, _baseProjectileDuration * multiplier);

            for (int i = 0; i < characterUnits.Count; i++)
            {
                var unit = characterUnits[i];
                if (unit == null || !unit.IsActive)
                {
                    continue;
                }

                SetNextAttackTime(unit, Time.time + attackInterval, true);
            }
        }

        public void ApplyDamageModifier(int value)
        {
            if (value <= 0)
            {
                return;
            }

            _damageBonusPoints += value;
            RefreshCombatDamage();
        }

        public void ApplyExplosionShotModifier(int value)
        {
            // ExplosionShot runtime state is managed centrally by GameplayManager.
        }

        public bool HasExplosionShot => GameplayManager.Instance != null && GameplayManager.Instance.IsExplosionShotUnlocked;

        public float ExplosionRadius => GameplayManager.Instance != null ? Mathf.Max(0f, GameplayManager.Instance.ExplosionShotRadius) : 0f;

        public int ResolveExplosionDamage(int baseDamage)
        {
            if (baseDamage <= 0 || GameplayManager.Instance == null || !GameplayManager.Instance.IsExplosionShotUnlocked)
            {
                return 0;
            }

            int percent = Mathf.Max(0, GameplayManager.Instance.ExplosionShotDamagePercent);
            if (percent <= 0)
            {
                return 0;
            }

            return Mathf.Max(1, Mathf.CeilToInt(baseDamage * (percent / 100f)));
        }

        public void UpgradeAllUnitsToLevel(int targetLevel, bool includeWeapon = true)
        {
            if (ArmyUpgradeManager.Instance != null)
            {
                ArmyUpgradeManager.Instance.SetLevel(targetLevel);
            }
        }

        private int ResolveCurrentArmyLevel()
        {
            int level = Mathf.Max(1, fallbackCharacterLevel);
            for (int i = 0; i < characterUnits.Count; i++)
            {
                var unit = characterUnits[i];
                if (unit == null || unit.Level <= 0)
                {
                    continue;
                }

                if (unit.Level > level)
                {
                    level = unit.Level;
                }
            }

            return level;
        }

        public void PlayAnimationForAllUnits(AnimationType animationType, float waitForAction = 0f, int layer = 0)
        {
            _unitSnapshotBuffer.Clear();
            _unitSnapshotBuffer.AddRange(characterUnits);

            for (int i = 0; i < _unitSnapshotBuffer.Count; i++)
            {
                var unit = _unitSnapshotBuffer[i];
                if (unit == null)
                {
                    continue;
                }

                unit.PlayAnimation(animationType, waitForAction, null, layer);
            }
        }

        public IReadOnlyList<CardSpawnRequestData> GetQueuedCardRequests() => null;

        public void ClearQueuedRequests() { }

        private void ResolveDependencies()
        {
            if (bodyRoot == null)
            {
                bodyRoot = transform;
            }

            if (inputManager == null)
            {
                inputManager = InputManager.Instance;
            }

            if (effectSystem == null)
            {
                effectSystem = GetComponentInChildren<PlayerArmyEffectSystem>(true);
            }

            if (characterUnits == null) characterUnits = new List<CharacterUnit>();
        }




        public void KillCurrentUnitsByPercentage(float percentageToRemain)
        {
            if (characterUnits == null || characterUnits.Count == 0) return;
            int remaining = Mathf.FloorToInt(characterUnits.Count * (percentageToRemain / 100f));
            KillCurrentUnitsToRemainingCount(remaining);
        }

        public void KillCurrentUnitsToRemainingCount(int remainingCount)
        {
            if (characterUnits == null || characterUnits.Count <= remainingCount) return;

            int toKill = characterUnits.Count - remainingCount;
            int killed = 0;

            GamePlay.Characters.CharacterUnit.IsBossMassKill = true;
            try
            {
                // Kill from the end of the list (outer units)
                for (int i = characterUnits.Count - 1; i >= 0 && killed < toKill; i--)
                {
                    var unit = characterUnits[i];
                    if (unit != null && unit.IsActive)
                    {
                        unit.OnHit(null); // Just kill it
                        killed++;
                    }
                }
            }
            finally
            {
                GamePlay.Characters.CharacterUnit.IsBossMassKill = false;
            }
        }

        public void ApplyLevelUpgrade(int levelIndex)
        {
            ApplyUnitCombatProfile();

            // Calculate and apply damage bonus based on level index
            _damageBonusPoints = levelIndex * damageBonusPerUpgrade;
            RefreshCombatDamage();

            _pendingVisualUpgradeLevel = levelIndex;
            _pendingVisualUpgradeIndex = 0;
        }

        private void ProcessPendingVisualUpgrades()
        {
            if (_pendingVisualUpgradeLevel < 0)
            {
                return;
            }

            int processed = 0;
            while (_pendingVisualUpgradeIndex < characterUnits.Count &&
                   processed < MaxVisualUpgradesPerFrame)
            {
                CharacterUnit unit = characterUnits[_pendingVisualUpgradeIndex++];
                if (unit == null || !unit.IsActive)
                {
                    continue;
                }

                unit.ApplyVisualLevel(_pendingVisualUpgradeLevel);
                processed++;
            }

            if (_pendingVisualUpgradeIndex >= characterUnits.Count)
            {
                _pendingVisualUpgradeLevel = -1;
                _pendingVisualUpgradeIndex = 0;
            }
        }
        // private void ClearSceneUnits()
        // {
        //     var sceneUnits = GetComponentsInChildren<CharacterUnit>(true);
        //     for (int i = 0; i < sceneUnits.Length; i++)
        //     {
        //         var unit = sceneUnits[i];
        //         if (unit == null)
        //         {
        //             continue;
        //         }
        //
        //         unit.RecycleImmediate(false);
        //     }
        //
        //     characterUnits.Clear();
        // }

        private void ResetRuntimeSpawnState()
        {
        }

        private Vector3 GetHoneycombSpawnPosition(Transform root, int index, int totalCount)
        {
            int cappedTotal = Mathf.Max(1, Mathf.Min(totalCount, Mathf.Min(maxActiveSpawnedUnits, HardMaxActiveSpawnedUnits)));
            int safeIndex = Mathf.Clamp(index, 0, cappedTotal - 1);
            if (safeIndex == 0)
            {
                return root.position;
            }

            int ring = 1;
            int remaining = safeIndex - 1;
            while (remaining >= 6 * ring)
            {
                remaining -= 6 * ring;
                ring++;
            }

            Vector2Int axial = GetHoneycombRingAxialPosition(ring, remaining);
            float spacing = Mathf.Max(0.01f, unitSpacing);
            float xStep = spacing;
            float zStep = spacing * HoneycombForwardStepFactor;

            Vector3 position = root.position;
            position += root.right * ((axial.x + axial.y * 0.5f) * xStep);
            position += root.forward * (axial.y * zStep);

            float jitterSeed = safeIndex * 0.61803398875f;
            float jitterX = (Mathf.PerlinNoise(jitterSeed, cappedTotal * 0.13f) - 0.5f) * spacing * 0.18f;
            float jitterZ = (Mathf.PerlinNoise(cappedTotal * 0.17f, jitterSeed) - 0.5f) * zStep * 0.18f;
            position += root.right * jitterX;
            position += root.forward * jitterZ;

            return position;
        }

        private static Vector2Int GetHoneycombRingAxialPosition(int ring, int offsetInRing)
        {
            ring = Mathf.Max(1, ring);
            int step = Mathf.Max(0, offsetInRing);
            var positions = GetOrBuildHoneycombRingPositions(ring);
            if (positions == null || positions.Length == 0)
                return Vector2Int.zero;
            return positions[Mathf.Clamp(step, 0, positions.Length - 1)];
        }

        private static Vector2Int[] GetOrBuildHoneycombRingPositions(int ring)
        {
            if (s_honeycombRingCache.TryGetValue(ring, out var cached) && cached != null && cached.Length > 0)
                return cached;

            var positions = new Vector2Int[ring * 6];
            int index = 0;
            Vector2Int axial = Vector2Int.zero;

            for (int i = 0; i < ring; i++)
                axial += HoneycombDirections[4];

            for (int side = 0; side < 6; side++)
            {
                for (int i = 0; i < ring; i++)
                {
                    positions[index++] = axial;
                    axial += HoneycombDirections[side];
                }
            }

            Array.Sort(positions, CompareHoneycombPosition);
            s_honeycombRingCache[ring] = positions;
            return positions;
        }

        private static int CompareHoneycombPosition(Vector2Int a, Vector2Int b)
        {
            float aZ = Mathf.Abs(a.y);
            float bZ = Mathf.Abs(b.y);
            if (!Mathf.Approximately(aZ, bZ))
                return aZ.CompareTo(bZ);

            if (a.y != b.y)
                return a.y.CompareTo(b.y);

            float aX = a.x + a.y * 0.5f;
            float bX = b.x + b.y * 0.5f;
            if (!Mathf.Approximately(aX, bX))
                return aX.CompareTo(bX);

            return a.x.CompareTo(b.x);
        }

        private void CacheDefaultState()
        {
            var root = GetBodyRoot();
            _targetX = root.localPosition.x;
            _currentForwardSpeed = fallbackForwardSpeed;
            _baseAttackInterval = Mathf.Max(0.05f, attackInterval);
            _baseProjectileDuration = Mathf.Max(0.05f, projectileDuration);
            _baseAttackDamage = Mathf.Max(1, attackDamage); // [FIX] Initialize from inspector
            _fireRateBonusPoints = 0;
            _baseFireRange = projectileDistance;
            _fireRangeBonus = 0f;
            _damageBonusPoints = 0;
            RefreshCombatDamage();
            RefreshFireRange();
        }

        private void UpdateMovement(float dt)
        {
            float targetSpeed = fallbackForwardSpeed;
            float speedChangeRate = Mathf.Max(0.01f, fallbackSpeedChangeRate);
            _currentForwardSpeed = Mathf.Lerp(_currentForwardSpeed, targetSpeed, dt * speedChangeRate);
            transform.position += transform.forward * (_currentForwardSpeed * dt);

            if (inputManager == null)
            {
                inputManager = InputManager.Instance;
            }

            float inputDelta = inputManager != null ? inputManager.GetMoveDelta() : 0f;
            float inputGain = Mathf.Clamp(inputSensitivity * 100f, 0.5f, 3f);
            float scaledInputDelta = inputDelta * inputGain;
            float tempTargetX = _targetX + (scaledInputDelta * strafeFollowMultiplier);
            tempTargetX = Mathf.Clamp(tempTargetX, -xLimit, xLimit);

            Transform root = GetBodyRoot();
            Vector3 localPos = root.localPosition;
            float baseSmoothness = 0.15f;
            float effectiveSmoothness = Mathf.Clamp01(baseSmoothness * Mathf.Max(1f, strafeFollowMultiplier) * (dt * 60f));
            float newX = Mathf.Lerp(localPos.x, tempTargetX, effectiveSmoothness);
            root.localPosition = new Vector3(newX, localPos.y, localPos.z);

            float lateralVelocity = (dt > 0f) ? (newX - localPos.x) / dt : 0f;
            _targetX = tempTargetX;

            for (int i = 0; i < characterUnits.Count; i++)
            {
                var unit = characterUnits[i];
                if (unit != null && unit.IsActive)
                {
                    CollisionSystem.NotifyMoved(unit);
                }
            }

            AnimationType targetAnimation = lateralVelocity < -LateralAnimationThreshold
                ? AnimationType.MoveLeft
                : lateralVelocity > LateralAnimationThreshold
                    ? AnimationType.MoveRight
                    : AnimationType.Attack;

            if (_currentMovementAnimation == targetAnimation)
            {
                return;
            }

            _currentMovementAnimation = targetAnimation;

            for (int i = 0; i < characterUnits.Count; i++)
            {
                var unit = characterUnits[i];
                if (unit != null && unit.IsActive)
                {
                    unit.PlayAnimation(targetAnimation, 0f, null, 0);
                }
            }
        }

        private void UpdateCollisionChecks()
        {
            var collisionSystem = CollisionSystem.Instance;
            if (collisionSystem == null || collisionSystem.Count <= 0)
            {
                _previousEnemyContactIds.Clear();
                _currentEnemyContactIds.Clear();
                return;
            }

            _currentEnemyContactIds.Clear();
            _currentEnvironmentContactIds.Clear();
            Vector3 myPos = Position;
            Vector2 mySize = Size;
            uint myMask = TargetMask;
            float myHalfX = mySize.x * 0.5f;
            float myHalfZ = mySize.y * 0.5f;
            float preCullX = Mathf.Max(myHalfX + 1f, collisionCheckRangeX);
            float preCullZ = Mathf.Max(myHalfZ + 1f, collisionCheckRangeZ);
            int formationUnitCount = Mathf.Min(characterUnits.Count, HardMaxActiveSpawnedUnits);
            Vector3 queryStart = myPos - transform.forward * preCullZ;
            Vector3 queryEnd = myPos + transform.forward * preCullZ;
            collisionSystem.QueryIndicesNearSegment(queryStart, queryEnd, preCullX, _collisionQueryIndices);

            for (int candidateIndex = 0; candidateIndex < _collisionQueryIndices.Count; candidateIndex++)
            {
                int i = _collisionQueryIndices[candidateIndex];
                uint categoryBits = collisionSystem.GetMask(i);
                if ((myMask & categoryBits) == 0)
                {
                    continue;
                }

                var targetTr = collisionSystem.GetTransform(i);
                if (targetTr == null)
                {
                    continue;
                }

                Vector3 tPos = targetTr.position;
                float distX = tPos.x - myPos.x;
                float distZ = tPos.z - myPos.z;
                float absDistX = Mathf.Abs(distX);
                float absDistZ = Mathf.Abs(distZ);
                if (absDistX > preCullX || absDistZ > preCullZ)
                {
                    continue;
                }

                var target = collisionSystem.GetTargetBySortedIndex(i);
                if (target == null || !target.IsActive || ReferenceEquals(target, this))
                {
                    continue;
                }

                var colData = collisionSystem.GetColliderData(i);
                float tHalfX = Mathf.Abs(colData.Size.x);
                float tHalfZ = Mathf.Abs(colData.Size.z);
                if (colData.Type != ShapeType.Box)
                {
                    tHalfZ = Mathf.Max(tHalfX, tHalfZ);
                    tHalfX = tHalfZ;
                }

                bool hitX = absDistX <= (myHalfX + tHalfX);
                bool hitZ = absDistZ <= (myHalfZ + tHalfZ);

                if (target.EntityType == EntityType.MovingGate &&
                    TryCollectFireSoldierWithFormation(target, targetTr, tPos, tHalfX, tHalfZ, formationUnitCount))
                {
                    continue;
                }

                if (!hitX || !hitZ)
                {
                    continue;
                }

                if (target.EntityType == EntityType.Enemy)
                {
                    int enemyInstanceId = targetTr.GetInstanceID();
                    _currentEnemyContactIds.Add(enemyInstanceId);

                    if (!_previousEnemyContactIds.Contains(enemyInstanceId))
                    {
                        ResolveEnemyContact(target, tPos);
                    }
                }
                else if (target.EntityType == EntityType.FinishTower)
                {
                    ResolveFinishTowerContact(target, tPos, tHalfX, tHalfZ);
                }
                else if (IsEnvironmentTarget(target.EntityType))
                {
                    int environmentInstanceId = targetTr.GetInstanceID();
                    _currentEnvironmentContactIds.Add(environmentInstanceId);

                    if (!_previousEnvironmentContactIds.Contains(environmentInstanceId))
                    {
                        target.OnHit(this);
                    }
                }
            }

            var tmpEnemy = _previousEnemyContactIds;
            _previousEnemyContactIds = _currentEnemyContactIds;
            _currentEnemyContactIds = tmpEnemy;

            var tmpEnv = _previousEnvironmentContactIds;
            _previousEnvironmentContactIds = _currentEnvironmentContactIds;
            _currentEnvironmentContactIds = tmpEnv;
        }

        private bool TryCollectFireSoldierWithFormation(
            IHitable target,
            Transform targetTransform,
            Vector3 targetPosition,
            float targetHalfX,
            float targetHalfZ,
            int formationUnitCount)
        {
            int targetId = targetTransform.GetInstanceID();
            if (!_fireSoldierGateCache.TryGetValue(targetId, out var gate) || gate == null)
            {
                gate = targetTransform.GetComponentInParent<GamePlay.Items.StatModifierGate>();
                _fireSoldierGateCache[targetId] = gate;
            }

            if (gate == null || gate.Data == null || gate.Data.Type != GamePlay.Items.StatType.Character)
            {
                return false;
            }

            if (formationUnitCount <= 0)
            {
                return false;
            }

            Transform root = GetBodyRoot();
            Vector3 localTargetPosition = root.InverseTransformPoint(targetPosition);
            GetFormationHalfExtents(formationUnitCount, out float formationHalfX, out float formationHalfZ);

            if (Mathf.Abs(localTargetPosition.x) > formationHalfX + targetHalfX ||
                Mathf.Abs(localTargetPosition.z) > formationHalfZ + targetHalfZ)
            {
                return false;
            }

            gate.CollectByArmy();
            return true;
        }

        private void GetFormationHalfExtents(int unitCount, out float halfX, out float halfZ)
        {
            int cappedCount = Mathf.Max(1, unitCount);
            int outerRing = Mathf.CeilToInt((Mathf.Sqrt(12f * cappedCount - 3f) - 3f) / 6f);
            float spacing = Mathf.Max(0.01f, unitSpacing);
            const float characterColliderPadding = 0.75f;

            halfX = outerRing * spacing * 1.5f + characterColliderPadding;
            halfZ = outerRing * spacing * HoneycombForwardStepFactor + characterColliderPadding;
        }

        private void UpdateCharacterAttacks()
        {
            if (characterUnits.Count == 0)
            {
                return;
            }

            bool isProjectileFireSuppressed = attackMode == PlayerArmyAttackMode.ThrownProjectile &&
                                              GamePlay.Items.NoProjectileFireZone.Contains(Position);
            if (isProjectileFireSuppressed)
            {
                _wasProjectileFireSuppressed = true;
                _pendingProjectileAttacks.Clear();
                return;
            }

            float now = Time.time;
            if (_wasProjectileFireSuppressed)
            {
                _wasProjectileFireSuppressed = false;
                _projectileFireResumeTime = now;
            }

            _frameAttackableTargets.Clear();
            _frameAttackableTransforms.Clear();
            _frameAttackableHalfWidths.Clear();
            bool needsDirectTargetCache = attackMode != PlayerArmyAttackMode.ThrownProjectile;
            var collisionSystem = needsDirectTargetCache ? CollisionSystem.Instance : null;
            if (collisionSystem != null && collisionSystem.Count > 0)
            {
                Vector2 attackWindow = ResolveAttackWindow();
                Vector3 armyPosition = Position;
                float attackRange = Mathf.Max(0.1f, attackWindow.y);
                collisionSystem.QueryIndicesNearSegment(
                    armyPosition - transform.forward * attackRange,
                    armyPosition + transform.forward * attackRange,
                    Mathf.Max(attackWindow.x, attackRange),
                    _collisionQueryIndices);

                for (int candidateIndex = 0; candidateIndex < _collisionQueryIndices.Count; candidateIndex++)
                {
                    int i = _collisionQueryIndices[candidateIndex];
                    var target = collisionSystem.GetTargetBySortedIndex(i);
                    if (target == null || !target.IsActive) continue;

                    var targetTr = collisionSystem.GetTransform(i);
                    if (targetTr == null) continue;

                    var colData = collisionSystem.GetColliderData(i);
                    uint categoryBits = colData.CategoryBits != 0 ? colData.CategoryBits : (uint)(1 << (int)target.EntityType);
                    if ((TargetMask & categoryBits) == 0) continue;

                    if (target is GamePlay.Items.StatModifierGate gate && gate.Data != null && gate.Data.Type == GamePlay.Items.StatType.Character) continue;

                    float targetHalfWidth = colData.Size.x > colData.Size.z ? colData.Size.x : colData.Size.z;
                    if (targetHalfWidth < 0) targetHalfWidth = -targetHalfWidth;

                    _frameAttackableTargets.Add(target);
                    _frameAttackableTransforms.Add(targetTr);
                    _frameAttackableHalfWidths.Add(targetHalfWidth);
                }
            }

            int totalUnits = characterUnits.Count;
            int maxEvals = Mathf.Clamp(maxAttackEvaluationsPerTick, 1, totalUnits);
            int evaluatedCount = 0;

            while (evaluatedCount < maxEvals)
            {
                if (_currentAttackEvalIndex >= totalUnits)
                {
                    _currentAttackEvalIndex = 0;
                }

                var unit = characterUnits[_currentAttackEvalIndex];
                _currentAttackEvalIndex++;
                evaluatedCount++;
                if (unit == null || !unit.IsActive)
                {
                    continue;
                }

                float nextAttackTime = Mathf.Max(unit.NextAttackTime, GetProjectileResumeAttackTime(unit));
                if (now < nextAttackTime)
                {
                    continue;
                }

                TryPerformAttack(unit);
                SetNextAttackTime(unit, now + Mathf.Max(0.05f, attackInterval));
            }
        }

        private bool TryPerformAttack(CharacterUnit unit)
        {
            switch (attackMode)
            {
                case PlayerArmyAttackMode.ThrownProjectile:
                    return TryPerformThrownProjectileAttack(unit);
                default:
                    return TryPerformDirectAttack(unit);
            }
        }

        private bool TryPerformDirectAttack(CharacterUnit unit)
        {
            if (unit == null || !unit.IsActive)
            {
                return false;
            }

            Vector2 attackWindow = ResolveAttackWindow();
            Vector3 origin = unit.transform.position + unit.transform.forward * Mathf.Max(0f, attackOriginOffset);
            if (!TryFindBestForwardTarget(unit, origin, Mathf.Max(0.1f, attackWindow.y), attackWindow.x, out var targetInfo))
            {
                return false;
            }

            //unit.PlayAnimation(AnimationType.Attack, 0f, null, 1);

            int effectiveDamage = ResolveEffectiveAttackDamage();
            var attackSource = GetUnitAttackSource();
            attackSource.SetupSource(unit.transform, origin, attackWindow, effectiveDamage, TargetMask);
            attackSource.OnAttackSucceed(targetInfo.Target);
            targetInfo.Target.OnHit(attackSource);
            OnAttackComplete?.Invoke(targetInfo.Target);
            attackSource.Dispose();
            if (effectSystem != null)
            {
                effectSystem.PlayEffectAt(EffectType.Attack, targetInfo.Position, Quaternion.identity, unit.transform, null, 0f);
            }

            return true;
        }

        private bool TryPerformThrownProjectileAttack(CharacterUnit unit)
        {
            if (unit == null || !unit.IsActive)
            {
                return false;
            }

            if (weaponProjectilePrefab == null)
            {
                return TryPerformDirectAttack(unit);
            }

            //unit.PlayAnimation(AnimationType.Attack, 0.4f, null, 1);

            _pendingProjectileAttacks.Add(new PendingProjectileAttack
            {
                Unit = unit,
                TriggerTime = Time.time + 0.4f
            });

            return true;
        }

        private void UpdatePendingProjectileAttacks()
        {
            if (currentState != PlayerArmyState.Active)
            {
                return;
            }

            if (_pendingProjectileAttacks.Count == 0) return;

            if (GamePlay.Items.NoProjectileFireZone.Contains(Position))
            {
                _wasProjectileFireSuppressed = true;
                _pendingProjectileAttacks.Clear();
                return;
            }

            float now = Time.time;
            int launchBudget = 0;

            // Limit projectile launches across multiple frames
            int currentFrame = Time.frameCount;
            if (_lastLaunchFrame < 0 || currentFrame - _lastLaunchFrame >= 2)
            {
                launchBudget = Mathf.Max(1, maxProjectileLaunchesPerFrame);
            }

            for (int i = _pendingProjectileAttacks.Count - 1; i >= 0 && launchBudget > 0; i--)
            {
                var attack = _pendingProjectileAttacks[i];
                if (now < attack.TriggerTime)
                {
                    continue;
                }

                ExecuteThrownProjectileAttack(attack.Unit);
                _lastLaunchFrame = currentFrame; // Record the launch frame
                int last = _pendingProjectileAttacks.Count - 1;
                if (i != last)
                {
                    _pendingProjectileAttacks[i] = _pendingProjectileAttacks[last];
                }
                _pendingProjectileAttacks.RemoveAt(last);
                launchBudget--;
            }
        }

        public void LaunchPlayerProjectile(CharacterUnit unit, Vector3 startPoint, Vector3 forward, Quaternion rotation, float distance, int damage)
        {
            if (weaponProjectilePrefab == null)
            {
                return;
            }

            var wp = weaponProjectilePrefab.Spawn(startPoint, rotation, null);
            if (wp == null)
            {
                return;
            }

            int currentLevelIndex = ArmyUpgradeManager.Instance != null ? ArmyUpgradeManager.Instance.CurrentLevel : 0;
            wp.ApplyVisualLevel(currentLevelIndex);

            wp.transform.localScale = unit.SelfScale * Mathf.Max(0f, unitscalevalue);
            wp.SetFly();

            if (!wp.Launch(
                    startPoint,
                    forward,
                    distance,
                    Mathf.Max(0.45f, projectileDuration),
                    0f,
                    0f,
                    damage,
                    EnemyProjectileSystem.ProjectileSpinAxis.None,
                    EnemyProjectileSystem.ProjectileMotionMode.Straight,
                    null,
                    false))
            {
                wp.Despawn();
                TryPerformDirectAttack(unit);
            }
        }

        private void ExecuteThrownProjectileAttack(CharacterUnit unit)
        {
            if (!GameplayManager.IsGameStarted || unit == null || !unit.IsActive)
            {
                return;
            }

            Vector3 forward = unit.transform.forward;
            Transform projectilePoint = unit.ProjectilePoint;
            Vector3 startPoint = projectilePoint != null
                ? projectilePoint.position
                : unit.transform.position + forward * Mathf.Max(0f, attackOriginOffset);
            Quaternion rotation = unit.transform.rotation;
            float distance = Mathf.Max(0.1f, projectileDistance);
            int damage = ResolveEffectiveAttackDamage();

            LaunchPlayerProjectile(unit, startPoint, forward, rotation, distance, damage);

            // Samurai Sword Skill Logic
            if (GameplayManager.Instance != null && GameplayManager.Instance.ActiveSamuraiBuffs.Count > 0)
            {
                if (Time.time - _lastSwordSkillIncrementTime >= _baseAttackInterval * 0.5f)
                {
                    _lastSwordSkillIncrementTime = Time.time;
                    for (int i = 0; i < GameplayManager.Instance.ActiveSamuraiBuffs.Count; i++)
                    {
                        var samuraiBuff = GameplayManager.Instance.ActiveSamuraiBuffs[i];
                        if (samuraiBuff != null && samuraiBuff.SamuraiConfig != null && samuraiBuff.AssociatedWeapon != null)
                        {
                            string buffId = string.IsNullOrEmpty(samuraiBuff.BuffId) ? samuraiBuff.name : samuraiBuff.BuffId;
                            if (!_samuraiAttackCounters.TryGetValue(buffId, out int count))
                            {
                                count = 0;
                            }
                            count++;

                            if (count >= samuraiBuff.SamuraiConfig.ShotThreshold)
                            {
                                count = 0;
                                // Phóng từng skill với delay nhỉnh hơn nhau để không bị trùng (0.15s, 0.3s...)
                                _pendingSwordSkills.Add(new DelayedSwordSkill
                                {
                                    WeaponPrefab = samuraiBuff.AssociatedWeapon,
                                    SamuraiConfig = samuraiBuff.SamuraiConfig,
                                    Unit = unit,
                                    StartPoint = startPoint,
                                    Forward = forward,
                                    Rotation = rotation,
                                    Distance = distance,
                                    Damage = damage * 2,
                                    RemainingDelay = 0.2f + (i * 0.15f)
                                });
                            }
                            _samuraiAttackCounters[buffId] = count;
                        }
                    }
                }
            }

            unit.PlayAttackEffect();
        }

        private struct ForwardTargetInfo
        {
            public IHitable Target;
            public Vector3 Position;
            public float ForwardDistance;
        }

        private bool TryFindBestForwardTarget(CharacterUnit unit, Vector3 origin, float range, float width, out ForwardTargetInfo result)
        {
            result = default;
            if (unit == null || _frameAttackableTargets.Count == 0)
            {
                return false;
            }

            Vector3 forward = unit.transform.forward;
            Vector3 right = unit.transform.right;
            float halfWidth = Mathf.Max(0.05f, width * 0.5f);
            float bestForward = float.MaxValue;
            IHitable bestTarget = null;
            Vector3 bestPosition = default;

            for (int i = 0; i < _frameAttackableTargets.Count; i++)
            {
                var target = _frameAttackableTargets[i];
                if (ReferenceEquals(target, unit))
                {
                    continue;
                }

                var targetTransform = _frameAttackableTransforms[i];
                float targetHalfWidth = _frameAttackableHalfWidths[i];

                Vector3 delta = targetTransform.position - origin;
                float forwardDistance = Vector3.Dot(delta, forward);
                if (forwardDistance < 0f || forwardDistance > range || forwardDistance >= bestForward)
                {
                    continue;
                }

                float lateralDistance = Vector3.Dot(delta, right);
                if (lateralDistance < 0) lateralDistance = -lateralDistance; // faster than Mathf.Abs

                if (lateralDistance > halfWidth + targetHalfWidth)
                {
                    continue;
                }

                bestForward = forwardDistance;
                bestTarget = target;
                bestPosition = targetTransform.position;
            }

            if (bestTarget == null)
            {
                return false;
            }

            result = new ForwardTargetInfo
            {
                Target = bestTarget,
                Position = bestPosition,
                ForwardDistance = bestForward
            };
            return true;
        }

        private Vector2 ResolveAttackWindow()
        {
            switch (attackMode)
            {
                case PlayerArmyAttackMode.Melee:
                    return meleeAttackSize;
                case PlayerArmyAttackMode.ForwardRanged:
                case PlayerArmyAttackMode.ThrownProjectile:
                default:
                    return new Vector2(rangedAttackSize.x, Mathf.Max(0.1f, projectileDistance));
            }
        }

        private void InitializeRuntimeUnit(CharacterUnit unit, int level, float? nextAttackTime = null, bool playMoveAnimation = false, bool refreshCombatProfile = true)
        {
            if (unit == null) return;

            if (!unit.gameObject.activeSelf)
            {
                unit.gameObject.SetActive(true);
            }
            if (unit.transform.parent != GetBodyRoot())
            {
                unit.transform.SetParent(GetBodyRoot(), true);
            }

            unit.Initialize(level, true);
            unit.Setup(level);

            int currentLevelIndex = ArmyUpgradeManager.Instance != null ? ArmyUpgradeManager.Instance.CurrentLevel : 0;

            unit.ApplyVisualLevel(currentLevelIndex);

            if (refreshCombatProfile)
            {
                ApplyUnitCombatProfile();
            }

            unit.PlayAnimation(ResolveRuntimeUnitAnimation(), 0f, null, 0);

            RegisterRuntimeUnit(unit);
            SetNextAttackTime(unit, nextAttackTime ?? (Time.time + attackInterval), !nextAttackTime.HasValue);

        }

        private AnimationType ResolveRuntimeUnitAnimation()
        {
            if (!GameplayManager.IsGameStarted || currentState != PlayerArmyState.Active)
            {
                return AnimationType.Idle;
            }

            switch (_currentMovementAnimation)
            {
                case AnimationType.MoveLeft:
                case AnimationType.MoveRight:
                case AnimationType.Attack:
                    return _currentMovementAnimation;
                default:
                    return AnimationType.Attack;
            }
        }

        private void RegisterRuntimeUnit(CharacterUnit unit)
        {
            if (unit == null)
            {
                return;
            }

            CollisionSystem.Register(unit, unit.transform);
        }

        private void UnregisterRuntimeUnit(CharacterUnit unit, bool deactivate)
        {
            if (unit == null)
            {
                return;
            }

            CollisionSystem.Unregister(unit);

            if (deactivate && unit.gameObject.activeInHierarchy)
            {
                unit.RecycleImmediate(false);
            }
            else if (unit.gameObject.activeInHierarchy)
            {
                unit.RecycleImmediate(false);
            }
        }

        private void PruneInactiveSpawnedUnits()
        {
            bool removedAny = false;
            for (int i = characterUnits.Count - 1; i >= 0; i--)
            {
                var unit = characterUnits[i];
                if (unit != null && unit.IsActive)
                {
                    continue;
                }

                UnregisterRuntimeUnit(unit, false);
                characterUnits.RemoveAt(i);
                removedAny = true;
            }

            if (removedAny)
            {

                TryTriggerLoseWhenArmyEmpty();
            }
        }

        private void TryTriggerLoseWhenArmyEmpty()
        {
            if (!GameplayManager.IsGameStarted)
            {
                return;
            }

            if (CountActiveUnits() > 0)
            {
                return;
            }

            if (GameplayManager.Instance != null)
            {
                GameplayManager.Instance.EndGame(false);
            }
        }

        private int GetNextAvailableArmyIndex()
        {
            BuildOccupiedArmyIndices();
            int index = FindNextAvailableArmyIndex();
            return index >= 0 ? index : characterUnits.Count;
        }

        private void BuildOccupiedArmyIndices()
        {
            Array.Clear(_occupiedArmyIndices, 0, _occupiedArmyIndices.Length);
            for (int i = 0; i < characterUnits.Count; i++)
            {
                var unit = characterUnits[i];
                if (unit == null || !unit.IsActive)
                {
                    continue;
                }

                int index = unit.ArmyIndex;
                if (index >= 0 && index < _occupiedArmyIndices.Length)
                {
                    _occupiedArmyIndices[index] = true;
                }
            }
        }

        private int FindNextAvailableArmyIndex()
        {
            int limit = Mathf.Min(_occupiedArmyIndices.Length, Mathf.Clamp(maxActiveSpawnedUnits, 1, HardMaxActiveSpawnedUnits));
            for (int i = 0; i < limit; i++)
            {
                if (!_occupiedArmyIndices[i])
                {
                    return i;
                }
            }

            return -1;
        }

        private Vector3 GetHoneycombLocalPosition(int index, int totalCount)
        {
            int cappedTotal = Mathf.Max(1, Mathf.Min(totalCount, Mathf.Min(maxActiveSpawnedUnits, HardMaxActiveSpawnedUnits)));
            int safeIndex = Mathf.Clamp(index, 0, cappedTotal - 1);
            if (safeIndex == 0) return Vector3.zero;

            int ring = 1;
            int remaining = safeIndex - 1;
            while (remaining >= 6 * ring)
            {
                remaining -= 6 * ring;
                ring++;
            }

            Vector2Int axial = GetHoneycombRingAxialPosition(ring, remaining);
            float spacing = Mathf.Max(0.01f, unitSpacing);
            float xStep = spacing;
            float zStep = spacing * HoneycombForwardStepFactor;

            Vector3 localPos = Vector3.zero;
            localPos += Vector3.right * ((axial.x + axial.y * 0.5f) * xStep);
            localPos += Vector3.forward * (axial.y * zStep);

            float jitterSeed = safeIndex * 0.61803398875f;
            float jitterX = (Mathf.PerlinNoise(jitterSeed, cappedTotal * 0.13f) - 0.5f) * spacing * 0.18f;
            float jitterZ = (Mathf.PerlinNoise(cappedTotal * 0.17f, jitterSeed) - 0.5f) * zStep * 0.18f;
            localPos += Vector3.right * jitterX;
            localPos += Vector3.forward * jitterZ;

            return localPos;
        }

        private bool TryReserveSpawnSlot()
        {
            PruneInactiveSpawnedUnits();
            int cap = Mathf.Clamp(maxActiveSpawnedUnits, 1, HardMaxActiveSpawnedUnits);
            return CountActiveUnits() < cap;
        }

        private void SetNextAttackTime(CharacterUnit unit, float nextAttackTime, bool addIndependentPhase = false)
        {
            if (unit == null)
            {
                return;
            }

            if (addIndependentPhase)
            {
                nextAttackTime += GetAttackPhase(unit) * Mathf.Max(0.05f, attackInterval);
            }

            unit.NextAttackTime = nextAttackTime;
        }

        private float GetProjectileResumeAttackTime(CharacterUnit unit)
        {
            if (attackMode != PlayerArmyAttackMode.ThrownProjectile ||
                float.IsNegativeInfinity(_projectileFireResumeTime))
            {
                return float.NegativeInfinity;
            }

            return _projectileFireResumeTime + GetAttackPhase(unit) * Mathf.Max(0.05f, attackInterval);
        }

        private static float GetAttackPhase(CharacterUnit unit)
        {
            unchecked
            {
                uint hash = (uint)unit.GetInstanceID();
                hash ^= hash >> 16;
                hash *= 0x7FEB352Du;
                hash ^= hash >> 15;
                hash *= 0x846CA68Bu;
                hash ^= hash >> 16;
                return (hash & 0x00FFFFFFu) * (1f / 16777216f);
            }
        }

        private bool IsEnvironmentTarget(EntityType entityType)
        {
            return entityType == EntityType.CapacityFactory ||
                   entityType == EntityType.CapacityGate ||
                   entityType == EntityType.ResourceTower ||
                   entityType == EntityType.PowerGate ||
                   entityType == EntityType.Item ||
                   entityType == EntityType.FinishTrigger ||
                   entityType == EntityType.GateNewEra ||
                   entityType == EntityType.MovingGate;
        }

        private void ResolveEnemyContact(IHitable enemyTarget, Vector3 enemyPos)
        {
            if (enemyTarget == null)
            {
                return;
            }

            if (TryGetClosestActiveCharacterUnit(enemyPos, out var victim))
            {
                victim.OnHit(enemyTarget as IAttacker ?? this);
            }

            enemyTarget.OnHit(this);
        }

        /// <summary>
        /// Khi army va chạm với FinishTower:
        /// - Lấy tất cả các unit có vị trí giao với tháp (cộng thêm unitRadius).
        /// - Giết các unit chạm tháp. Nếu số lượng unit <= số lượng va chạm, giữ lại 1 unit cuối.
        /// - Khi unit cuối cùng chạm tháp, trigger EndGame.
        /// </summary>
        private void ResolveFinishTowerContact(IHitable towerTarget, Vector3 towerPos, float tHalfX, float tHalfZ)
        {
            if (towerTarget == null)
            {
                return;
            }

            int activeCount = CountActiveUnits();
            if (activeCount == 0)
            {
                towerTarget.OnHit(this);
                return;
            }

            float unitRadius = 0.6f; // Dựa trên unitSpacing / 2 
            _finishTowerHitUnitsBuffer.Clear();

            for (int i = 0; i < characterUnits.Count; i++)
            {
                var unit = characterUnits[i];
                if (unit == null || !unit.IsActive) continue;

                Vector3 unitPos = unit.Position;
                float distX = Mathf.Abs(unitPos.x - towerPos.x);
                float distZ = Mathf.Abs(unitPos.z - towerPos.z);

                if (distX <= (tHalfX + unitRadius) && distZ <= (tHalfZ + unitRadius))
                {
                    _finishTowerHitUnitsBuffer.Add(unit);
                }
            }

            if (_finishTowerHitUnitsBuffer.Count == 0)
            {
                return; // Army bounding box chạm nhưng không có unit nào thực sự chạm
            }

            // Nếu số unit đang active <= số unit va chạm => giữ lại 1 unit
            int unitsToKill = Mathf.Min(_finishTowerHitUnitsBuffer.Count, activeCount - 1);

            for (int i = 0; i < unitsToKill; i++)
            {
                _finishTowerHitUnitsBuffer[i].RecycleImmediate(true);
            }

            // Đảm bảo tháp nhận sát thương / sự kiện va chạm từ army
            towerTarget.OnHit(this);

            // Nếu đây là unit cuối cùng chạm vào tháp, gọi EndGame
            if (activeCount - unitsToKill <= 1)
            {
                int towerId = towerTarget.GetHashCode();
                int frame = Time.frameCount;
                if (_finishTowerLastHitFrame != frame)
                {
                    _finishTowerLastHitFrame = frame;
                    _finishTowerHitIdsThisFrame.Clear();
                }

                if (_finishTowerHitIdsThisFrame.Add(towerId))
                {
                    if (GameplayManager.Instance != null)
                    {
                        GameplayManager.Instance.EndGame(true);
                    }
                }
            }
        }

        /// <summary>
        /// Đếm số unit đang active trong characterUnits.
        /// </summary>
        private int CountActiveUnits()
        {
            int count = 0;
            for (int i = 0; i < characterUnits.Count; i++)
            {
                var unit = characterUnits[i];
                if (unit != null && unit.IsActive)
                {
                    count++;
                }
            }
            return count;
        }

        private bool TryGetClosestActiveCharacterUnit(Vector3 enemyPos, out CharacterUnit victim)
        {
            victim = null;

            float bestDistance = float.MaxValue;
            for (int i = 0; i < characterUnits.Count; i++)
            {
                var unit = characterUnits[i];
                if (unit == null || !unit.IsActive)
                {
                    continue;
                }

                Vector3 delta = unit.Position - enemyPos;
                delta.y = 0f;
                float distance = delta.sqrMagnitude;
                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                victim = unit;
            }

            return victim != null;
        }

        private static readonly Queue<UnitAttackSource> _attackSourcePool = new Queue<UnitAttackSource>(64);

        private UnitAttackSource GetUnitAttackSource()
        {
            return _attackSourcePool.Count > 0 ? _attackSourcePool.Dequeue() : new UnitAttackSource(this);
        }

        private static void ReturnUnitAttackSource(UnitAttackSource source)
        {
            if (source != null)
            {
                _attackSourcePool.Enqueue(source);
            }
        }

        private Transform GetBodyRoot()
        {
            return bodyRoot != null ? bodyRoot : transform;
        }

        private sealed class UnitAttackSource : IAttacker
        {
            public event Action<IHitable> OnAttackComplete = delegate { };

            private readonly PlayerArmySystem _owner;
            private Transform _transform;
            private Vector3 _position;
            private Vector2 _size;
            private int _damage;
            private uint _targetMask;

            public UnitAttackSource(PlayerArmySystem owner)
            {
                _owner = owner;
            }

            public void SetupSource(Transform transform, Vector3 position, Vector2 size, int damage, uint targetMask)
            {
                _transform = transform;
                _position = position;
                _size = size;
                _damage = Mathf.Max(1, damage);
                _targetMask = targetMask;
            }

            public Transform Transform => _transform;
            public EntityType EntityType => EntityType.Character;
            public Vector2 Size => _size;
            public int Damage => _damage;
            public uint TargetMask => _targetMask;
            public Vector3 Position => _position;

            public void Initialize()
            {
            }

            public void Dispose()
            {
                ReturnUnitAttackSource(this);
            }

            public void Setup(int damage)
            {
            }

            public void OnAttackSucceed(IHitable target)
            {
                OnAttackComplete?.Invoke(target);
            }
        }
    }
}
