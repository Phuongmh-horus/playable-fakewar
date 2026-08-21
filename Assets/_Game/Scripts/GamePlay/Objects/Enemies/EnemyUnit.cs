using System;
using GamePlay.AnimationSystems;
using GamePlay.CombatSystems;
using GamePlay.Entities;
using GamePlay.Items;
using GamePlay.Weapons;
using GamePlay.HealthSystems;
using GamePlay.CollisionSystems;
using GamePlay.ComponentSystems;
using GamePlay.Effects;
using DG.Tweening;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Pools;


namespace GamePlay.Enemies

{

    public class EnemyUnit : ItemUnit
    {
        private static readonly List<MonoBehaviour> s_enemyBuffer = new List<MonoBehaviour>(64);

        [Header("Animation Settings")]
        [SerializeField] protected float waitAttackAnimation = 1f;
        [SerializeField] protected bool isHandleKillHero;
        [SerializeField] protected bool isKillHeroAsPercent;
        [SerializeField] protected float heroToRemain = 3f;
        protected bool _isAttacked;
        private IHitable _pendingArmyHitTarget;

        public WeaponUnit WeaponPrefab;
        public Transform HandTransform;

        [SerializeField] private SpriteRenderer hpBarRenderer;
        [SerializeField] protected TextMeshPro healthText;
        [SerializeField] private HitTextFlyEffect hitTextFlyEffect;


        [Header("HP Bar Settings")]
        [SerializeField] private GameObject _healthBarRoot;

        [SerializeField] private int defaultMaxHealth = 3;



        [Header("Death VFX")]

        [SerializeField] private GameObject dieVfxPrefab;
        public GameObject DieVfxPrefab => dieVfxPrefab;

        [SerializeField] private Vector3 dieVfxOffset = Vector3.zero;

        [SerializeField] private float dieVfxLifetime = 1.2f;

        [SerializeField] private int maxDeathVfxPerFrame = 8;

        private static int s_lastDeathVfxFrame = -1;

        private static int s_deathVfxCountInFrame = 0;

        private static int s_lastDieSfxFrame = -1;
        private const int DieEffectFrameInterval = 20;

        private WeaponUnit _currentWeapon;

        private bool _despawnHandled;
        private bool _deathVfxHandled;

        private bool _initialized; // [FIX] Prevent double initialization in Luna

        protected HealthComponent _healthComponent;

        private Vector3 _originalBarScale;

        private Vector3 _originalLocalPos;
        [SerializeField, HideInInspector] private bool _healthOverriddenFromContent;



#if UNITY_EDITOR

        // Không override để tránh lỗi nếu ItemUnit.OnValidate() không virtual

        protected override void OnValidate()
        {
            base.OnValidate();

            if (HandTransform == null)
            {
                HandTransform = FindChildContains(transform, "WeaponHolder");
            }


            // [FIX] Auto-set EntityType for Enemy if not already set

            if (_entityType == EntityType.None)

            {

                _entityType = EntityType.Enemy;

            }



            EnsureHitTextEffect(false);

        }

#endif



        protected override void Awake()

        {

            base.Awake();

            // [FIX] Ensure EntityType is Enemy at runtime

            if (_entityType == EntityType.None)

            {

                _entityType = EntityType.Enemy;

            }

        }



        private static Transform FindChildContains(Transform root, string contains)

        {

            if (root == null) return null;



            for (int i = 0; i < root.childCount; i++)

            {

                var c = root.GetChild(i);

                if (c.name != null && c.name.Contains(contains))

                    return c;



                var sub = FindChildContains(c, contains);

                if (sub != null) return sub;

            }



            return null;

        }



        private void OnDisable()

        {

            // Ensure weapon cleanup happens when enemy is returned to pool / disabled.

            DespawnInterval();

        }



        public override void Initialize()
        {
            // [FIX] Prevent double initialization in Luna
            if (_initialized) return;
            _initialized = true;

            base.Initialize();
            Pack.Animator?.PlayAnimation(AnimationType.Idle, 0f, null, 0); // [FIX] Start with Idle for enemies

            // [FIX] Ensure Hit capability and register to CollisionSystem explicitly
            ActiveFlags |= CapabilityFlags.Hit;
            if (Pack.Hitable != null)
            {
                CollisionSystem.Register(Pack.Hitable, transform);
            }

            // giữ nguyên logic register
            if (EnemyManager.Instance != null)
                EnemyManager.Instance.RegisterEnemy(this);

            _healthComponent = Pack.Healable as HealthComponent;
            EnsureHitTextEffect(false);
            _despawnHandled = false;
            _deathVfxHandled = false;
            _isAttacked = false;
            _pendingArmyHitTarget = null;

            if (hitTextFlyEffect != null)
                hitTextFlyEffect.enabled = true;

            if (hpBarRenderer != null)
            {
                hpBarRenderer.sortingOrder = 50;

                // Cache Scale FIRST
                _originalBarScale = hpBarRenderer.transform.localScale;
                _originalLocalPos = hpBarRenderer.transform.localPosition;
                _healthBarRoot = hpBarRenderer.transform.parent != null
                    ? hpBarRenderer.transform.parent.gameObject
                    : hpBarRenderer.gameObject;

                // Initialize Visuals
                if (_healthComponent != null)
                    UpdateImage(_healthComponent.CurrentHealth, _healthComponent.MaxHealth);
                else
                    UpdateImage(defaultMaxHealth, defaultMaxHealth);
            }

            UpdateHealthText(_healthComponent != null ? _healthComponent.CurrentHealth : defaultMaxHealth);
        }


        protected override void DespawnInterval()

        {

            // Debug.Log($"[EnemyUnit] DespawnInterval called on {name}. Handled? {_despawnHandled}");

            if (_despawnHandled) return;

            _despawnHandled = true;
            _pendingArmyHitTarget = null;



            // [FIX] Reset initialization flag for pool reuse

            _initialized = false;



            if (EnemyManager.Instance != null)

                EnemyManager.Instance.UnregisterEnemy(this);



            if (_healthComponent != null)

            {

                _healthComponent.OnHealthChange -= HandleHealthChange;

            }



            base.DespawnInterval();

        }



        // ...



        // [FIX] Play VFX on Wheel Collision (Instant Death)

        protected override void HandleWheelCollision()
        {
            PlayDeathVfx();
            base.HandleWheelCollision();
        }

        public virtual void HandlePlayerArmyMeleeContact(IAttacker armySource)
        {
            if (_isAttacked) return;

            _isAttacked = true;
            _pendingArmyHitTarget = armySource as IHitable;
            PlayAnimation(AnimationType.Attack, waitAttackAnimation, CompleteArmyMeleeAttack, 0);
        }

        private void CompleteArmyMeleeAttack()
        {
            if (_pendingArmyHitTarget != null)
            {
                _pendingArmyHitTarget.OnHit(this);
            }
            else if (GameplayManager.Instance != null && GameplayManager.Instance.ActiveArmy != null)
            {
                var army = GameplayManager.Instance.ActiveArmy;
                if (army.Units.Count > 0)
                {
                    army.Units[0].OnHit(this);
                }
            }

            _pendingArmyHitTarget = null;
            DespawnInterval();
        }


        public void PlayAnimation(AnimationType animationType, float waitForAction = 0.5f, Action onComplete = null, int layer = 0)
        {
            if (Pack.Animator != null)
                Pack.Animator.PlayAnimation(animationType, waitForAction, onComplete, layer);

        }

        public void AttachWeapon(WeaponUnit weaponUnit)

        {

            _currentWeapon = weaponUnit;

        }

        public void ThrowWeapon()

        {

            _currentWeapon = null;

        }

        protected override void HandleHealthChange(int current, int max)
        {
            UpdateImage(current, max);
            UpdateHealthText(current);


            // [FIX] Spawn VFX ONLY on death (Health <= 0)

            if (current <= 0)
            {
                PlayDeathVfx();
                PlayDieEffectPerFrame();
            }


            base.HandleHealthChange(current, max);
        }

        public void SetHealthOverLevel(int maxHealth)
        {
            HealthComponent healthComponent = Pack.Healable as HealthComponent;
            if (healthComponent == null)
            {
                healthComponent = GetComponent<HealthComponent>();
            }

            if (healthComponent == null) return;

            healthComponent.SetMaxHealth(maxHealth, refill: true);
            UpdateImage(healthComponent.CurrentHealth, healthComponent.MaxHealth);
            UpdateHealthText(healthComponent.CurrentHealth);
        }

        protected void HandleKillHero()
        {
            if (!isHandleKillHero) return;

            var army = GameplayManager.Instance != null ? GameplayManager.Instance.ActiveArmy : null;
            if (army == null) return;

            if (isKillHeroAsPercent)
            {
                army.KillCurrentUnitsByPercentage(heroToRemain);
            }
            else
            {
                army.KillCurrentUnitsToRemainingCount((int)heroToRemain);
            }
        }

        protected void PlayDieEffectPerFrame()
        {
            int currentFrame = Time.frameCount;
            if (s_lastDieSfxFrame >= 0 && currentFrame - s_lastDieSfxFrame < DieEffectFrameInterval)
            {
                return;
            }

            s_lastDieSfxFrame = currentFrame;
            Pack.Effector?.PlayEffect(EffectType.Die, transform.position + Vector3.up * 1f, transform.rotation);
        }


        protected void UpdateImage(int currentHealth, int maxHealth)

        {

            if (hpBarRenderer == null) return;

            SetHealthBarVisible(maxHealth > 0 && currentHealth < maxHealth);



            // [FIX] Simple Transform Scaling with Pivot Correction

            float healthPercent = maxHealth > 0 ? (float)currentHealth / maxHealth : 0f;



            // Capture original state

            if (_originalBarScale == Vector3.zero)

            {

                _originalBarScale = hpBarRenderer.transform.localScale;

                _originalLocalPos = hpBarRenderer.transform.localPosition;

            }



            Vector3 targetScale = _originalBarScale;

            targetScale.x *= healthPercent;



            hpBarRenderer.transform.localScale = targetScale;



            // [FIX] Compensate for Center Pivot (Sprite shrinks from both sides)

            // Shift position LEFT to keep the left edge stationary.

            // Formula: Shift = (NewScale - OldScale) * Width * 0.5

            if (hpBarRenderer.sprite != null)

            {

                float spriteWidth = hpBarRenderer.sprite.bounds.size.x;

                float scaleDiff = targetScale.x - _originalBarScale.x; // Negative when shrinking

                float shift = scaleDiff * spriteWidth * 0.5f;



                // [FIX] Inverted direction per user request ("Đảo lại đi")

                // Current: Move Right (shift is negative, so -shift is positive).

                hpBarRenderer.transform.localPosition = _originalLocalPos - new Vector3(shift, 0, 0);

            }

        }

        private void SetHealthBarVisible(bool visible)
        {
            if (_healthBarRoot == null)
            {
                _healthBarRoot = hpBarRenderer.transform.parent != null
                    ? hpBarRenderer.transform.parent.gameObject
                    : hpBarRenderer.gameObject;
            }

            if (_healthBarRoot.activeSelf != visible)
            {
                _healthBarRoot.SetActive(visible);
            }
        }



        public void MarkHealthOverriddenFromContent()
        {
            _healthOverriddenFromContent = true;
        }

        protected void UpdateHealthText(int health)
        {
            if (healthText == null) return;

            healthText.SetText(health > 0 ? "{0}" : string.Empty, health);
        }


        protected void PlayDeathVfx()

        {

            if (dieVfxPrefab == null)

                return;

            if (_deathVfxHandled)

                return;

            _deathVfxHandled = true;

            if (!CanSpawnDeathVfxThisFrame())

                return;



            Vector3 spawnPos = transform.position + dieVfxOffset;

            GameObject vfx = dieVfxPrefab.Spawn();

            if (vfx == null) return;



            vfx.transform.position = spawnPos;

            vfx.transform.rotation = Quaternion.identity;

            vfx.SetActive(true);



            DOVirtual.DelayedCall(Mathf.Max(0.05f, dieVfxLifetime), () =>
            {
                if (vfx != null) vfx.Despawn();
            }, false).SetId(vfx);

        }

        private bool CanSpawnDeathVfxThisFrame()

        {

            if (Time.frameCount != s_lastDeathVfxFrame)

            {

                s_lastDeathVfxFrame = Time.frameCount;

                s_deathVfxCountInFrame = 0;

            }



            int frameCap = Mathf.Max(1, maxDeathVfxPerFrame);

            if (s_deathVfxCountInFrame >= frameCap)

                return false;



            s_deathVfxCountInFrame++;

            return true;

        }



        private void EnsureHitTextEffect(bool allowAddRuntime)

        {

            if (hitTextFlyEffect != null) return;

            hitTextFlyEffect = GetComponentInChildren<HitTextFlyEffect>(true);

            if (hitTextFlyEffect == null && allowAddRuntime)

                hitTextFlyEffect = gameObject.AddComponent<HitTextFlyEffect>();

        }

    }

}



