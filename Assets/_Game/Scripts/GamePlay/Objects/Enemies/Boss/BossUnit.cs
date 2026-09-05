using System;
using GamePlay.AnimationSystems;
using GamePlay.ComponentSystems;
using GamePlay.Entities;
using UnityEngine;

namespace GamePlay.Enemies
{
    public class BossUnit : EnemyUnit
    {
        private static readonly Vector3 AttackEffectLocalPosition = new Vector3(1.25f, 5f, 2f);
        private static readonly Quaternion AttackEffectLocalRotation = Quaternion.Euler(0f, 209.6f, -195.536f);

        public static event Action<float> OnHealthChanged = delegate { };

        public static event Action OnWheelCollision = delegate { };

        [Header("Boss Settings")]
        [SerializeField, Min(0f)] private float delayBetweenAttacks = 1f;
        [SerializeField, Min(0f)] private float bossAttractionThreshold = 15f;
        [SerializeField, Min(0.1f)] private float armyAttackRange = 1f;
        [SerializeField, Min(0f)] private float deathAnimationDuration = 1f;
        [SerializeField] private float attackEffectDelay = 0.12f;
        [SerializeField, Min(0.05f), Tooltip("Minimum interval between boss hit VFX spawns.")]
        private float hitEffectCooldown = 0.12f;

        public float AttractionThreshold => bossAttractionThreshold;

        private float _nextAttackTime;
        private float _nextArmyTargetScanTime;
        private bool _deathHandled;
        private bool _hasEngagedArmy;
        private IHitable _pendingArmyHitTarget;
        private float _nextHitEffectTime;
        private float _scheduledAttackEffectTime = -1f;

        public override void Initialize()
        {
            if (_entityType == EntityType.None || _entityType == EntityType.Enemy)
            {
                _entityType = EntityType.Boss;
            }

            base.Initialize();

            // Disable fly text on boss per user request
            var flyText = GetComponentInChildren<HitTextFlyEffect>(true);
            if (flyText != null)
            {
                flyText.enabled = false;
            }

            if (_healthComponent != null)
            {
                _healthComponent.OnTakeDamaged -= HandleBossDamaged;
                _healthComponent.OnTakeDamaged += HandleBossDamaged;
            }

            _nextAttackTime = 0f;
            _deathHandled = false;
            _isAttacked = false;
            _hasEngagedArmy = false;
            _pendingArmyHitTarget = null;
            _scheduledAttackEffectTime = -1f;
        }

        protected override void DespawnInterval()
        {
            if (_healthComponent != null)
            {
                _healthComponent.OnTakeDamaged -= HandleBossDamaged;
            }

            _scheduledAttackEffectTime = -1f;

            base.DespawnInterval();
        }

        private void HandleBossDamaged(int damage)
        {
            if (damage > 0 && Time.time >= _nextHitEffectTime)
            {
                _nextHitEffectTime = Time.time + Mathf.Max(0.05f, hitEffectCooldown);
                Vector3 hitPos = transform.position + transform.forward * 1.5f;
                Pack.Effector?.PlayEffect(EffectType.Hit, hitPos, transform.rotation);
            }
        }

        private void FixedUpdate()
        {
            TryPlayScheduledAttackEffect();

            if ((Pack.Healable?.IsDead ?? false) || _deathHandled || _isAttacked || Time.time < _nextAttackTime) return;

            if (Time.time < _nextArmyTargetScanTime)
            {
                return;
            }
            TryPlayScheduledAttackEffect();
            _nextArmyTargetScanTime = Time.time + 0.1f;

            if (TryGetClosestActiveArmyAttacker(_hasEngagedArmy, out var armyAttacker))
            {
                Pack.Mover?.OnMovementFinished();
                TryAttackArmy(armyAttacker);
                return;
            }

            _hasEngagedArmy = false;
        }

        protected override void HandleWheelCollision()
        {
            if ((Pack.Healable?.IsDead ?? false) || _deathHandled || _isAttacked || Time.time < _nextAttackTime)
            {
                return;
            }

            _isAttacked = true;
            _hasEngagedArmy = true;
            PlayableWaveDefenseEntitySystem.Instance?.Unregister(this);
            PlayAnimation(AnimationType.Attack, waitForAction: waitAttackAnimation, onComplete: CompleteWheelAttack);
            ScheduleAttackEffect();
        }

        public override void HandlePlayerArmyMeleeContact(IAttacker armySource)
        {
            _hasEngagedArmy = true;
            TryAttackArmy(armySource);
        }

        protected override void HandleHealthChange(int current, int max)
        {
            OnHealthChanged?.Invoke(current);

            if (current > 0)
            {
                base.HandleHealthChange(current, max);
                return;
            }

            if (_deathHandled)
            {
                return;
            }

            _deathHandled = true;
            _isAttacked = true;
            UpdateImage(current, max);
            UpdateHealthText(current);
            _scheduledAttackEffectTime = -1f;
            PlayDeathVfx();
            PlayAnimation(AnimationType.Death, deathAnimationDuration, DespawnInterval);
            Pack.Effector?.PlayEffect(EffectType.Die, transform.position, transform.rotation);
            PlayDieEffectPerFrame();
        }

        protected override bool ShouldShowHealthUi(int currentHealth, int maxHealth)
        {
            return maxHealth > 0;
        }

        private void TryAttackArmy(IAttacker armySource)
        {
            if ((Pack.Healable?.IsDead ?? false) || _deathHandled || _isAttacked || Time.time < _nextAttackTime)
            {
                return;
            }

            _isAttacked = true;
            _hasEngagedArmy = true;
            _pendingArmyHitTarget = armySource as IHitable;
            PlayableWaveDefenseEntitySystem.Instance?.Unregister(this);
            PlayAnimation(AnimationType.Attack, waitForAction: waitAttackAnimation, onComplete: CompleteArmyAttack);
            ScheduleAttackEffect();
        }

        private void ScheduleAttackEffect()
        {
            _scheduledAttackEffectTime = Time.time + attackEffectDelay;
        }

        private void TryPlayScheduledAttackEffect()
        {
            if (_scheduledAttackEffectTime < 0f || Time.time < _scheduledAttackEffectTime)
            {
                return;
            }

            _scheduledAttackEffectTime = -1f;
            if (_deathHandled || (Pack.Healable?.IsDead ?? false))
            {
                return;
            }

            PlayAttackEffect();
        }

        private void PlayAttackEffect()
        {
            Vector3 worldPosition = transform.TransformPoint(AttackEffectLocalPosition);
            Quaternion worldRotation = transform.rotation * AttackEffectLocalRotation;
            Pack.Effector?.PlayEffect(EffectType.Attack, worldPosition, worldRotation, transform);
        }

        private void CompleteWheelAttack()
        {
            if (_deathHandled || (Pack.Healable?.IsDead ?? false))
            {
                return;
            }

            _scheduledAttackEffectTime = -1f;
            HandleKillHero();
            if (!isHandleKillHero)
            {
                OnWheelCollision?.Invoke();
            }

            FinishAttackCycle();
        }

        private void CompleteArmyAttack()
        {
            if (_deathHandled || (Pack.Healable?.IsDead ?? false))
            {
                _pendingArmyHitTarget = null;
                return;
            }

            _scheduledAttackEffectTime = -1f;
            HandleKillHero();
            if (!isHandleKillHero)
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
            }

            _pendingArmyHitTarget = null;
            FinishAttackCycle();
        }

        private bool TryGetClosestActiveArmyAttacker(bool allowAnyDistance, out IAttacker armyAttacker)
        {
            armyAttacker = null;

            var army = GameplayManager.Instance?.ActiveArmy;
            if (army == null || army.Units.Count == 0)
            {
                return false;
            }

            float closestDistSqr = float.MaxValue;
            GamePlay.Characters.CharacterUnit closestUnit = null;
            Vector3 bossPosition = transform.position;

            for (int i = 0; i < army.Units.Count; i++)
            {
                var unit = army.Units[i];
                if (unit == null || !unit.IsActive || (unit.Pack.Healable?.IsDead ?? false))
                {
                    continue;
                }

                Vector3 delta = unit.transform.position - bossPosition;
                float distSqr = delta.sqrMagnitude;
                if (distSqr < closestDistSqr)
                {
                    closestDistSqr = distSqr;
                    closestUnit = unit;
                }
            }

            if (closestUnit == null)
            {
                return false;
            }

            if (!allowAnyDistance && closestDistSqr > armyAttackRange * armyAttackRange)
            {
                return false;
            }

            armyAttacker = closestUnit.Pack.Attacker;
            return true;
        }

        private void FinishAttackCycle()
        {
            _pendingArmyHitTarget = null;
            _isAttacked = false;
            _nextAttackTime = Time.time + Mathf.Max(0f, delayBetweenAttacks);
        }
    }
}
