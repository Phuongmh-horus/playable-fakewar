using System;
using GamePlay.AnimationSystems;
using GamePlay.ComponentSystems;
using GamePlay.Entities;
using UnityEngine;

namespace GamePlay.Enemies
{
    public class BossUnit : EnemyUnit
    {
        public static event Action<float> OnHealthChanged = delegate { };

        public static event Action OnWheelCollision = delegate { };

        [Header("Boss Settings")]
        [SerializeField, Min(0f)] private float delayBetweenAttacks = 1f;
        [SerializeField, Min(0.1f)] private float armyAttackRange = 1f;
        [SerializeField, Min(0f)] private float deathAnimationDuration = 1f;

        private float _nextAttackTime;
        private bool _deathHandled;
        private bool _hasEngagedArmy;

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
        }

        protected override void DespawnInterval()
        {
            if (_healthComponent != null)
            {
                _healthComponent.OnTakeDamaged -= HandleBossDamaged;
            }

            base.DespawnInterval();
        }

        private void HandleBossDamaged(int damage)
        {
            if (damage > 0)
            {
                // Play EffectType.Hit with a forward offset
                Vector3 hitPos = transform.position + transform.forward * 1.5f;
                Pack.Effector?.PlayEffect(EffectType.Hit, hitPos, transform.rotation);
            }
        }

        private void FixedUpdate()
        {
            if ((Pack.Healable?.IsDead ?? false) || _deathHandled || _isAttacked || Time.time < _nextAttackTime) return;

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
            SoundManager.Instance?.PlayOneShot(AudioClipName.SFX_EnemyAttack);

            PlayAnimation(AnimationType.Attack, waitForAction: waitAttackAnimation, onComplete: () =>
            {
                if (_deathHandled || (Pack.Healable?.IsDead ?? false))
                {
                    return;
                }

                HandleKillHero();
                if (!isHandleKillHero)
                {
                    OnWheelCollision?.Invoke();
                }

                FinishAttackCycle();
            });
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
            PlayDeathVfx();
            PlayAnimation(AnimationType.Death, deathAnimationDuration, DespawnInterval);
            Pack.Effector?.PlayEffect(EffectType.Die, transform.position, transform.rotation);
            PlayDieEffectOncePerFrame();
        }

        private void TryAttackArmy(IAttacker armySource)
        {
            if ((Pack.Healable?.IsDead ?? false) || _deathHandled || _isAttacked || Time.time < _nextAttackTime)
            {
                return;
            }

            _isAttacked = true;
            _hasEngagedArmy = true;
            PlayableWaveDefenseEntitySystem.Instance?.Unregister(this);
            SoundManager.Instance?.PlayOneShot(AudioClipName.SFX_EnemyAttack);

            PlayAnimation(AnimationType.Attack, waitForAction: waitAttackAnimation, onComplete: () =>
            {
                if (_deathHandled || (Pack.Healable?.IsDead ?? false))
                {
                    return;
                }

                HandleKillHero();
                if (!isHandleKillHero)
                {
                    if (armySource is IHitable hitableArmy)
                    {
                        hitableArmy.OnHit(this);
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

                FinishAttackCycle();
            });
        }

        private bool TryGetClosestActiveArmyAttacker(bool allowAnyDistance, out IAttacker armyAttacker)
        {
            armyAttacker = null;

            var army = GameplayManager.Instance?.ActiveArmy;
            if (army == null || army.Units.Count == 0)
            {
                return false;
            }

            float closestDist = float.MaxValue;
            GamePlay.Characters.CharacterUnit closestUnit = null;

            for (int i = 0; i < army.Units.Count; i++)
            {
                var unit = army.Units[i];
                if (unit == null || !unit.IsActive || (unit.Pack.Healable?.IsDead ?? false))
                {
                    continue;
                }

                float dist = Vector3.Distance(transform.position, unit.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestUnit = unit;
                }
            }

            if (closestUnit == null)
            {
                return false;
            }

            if (!allowAnyDistance && closestDist > armyAttackRange)
            {
                return false;
            }

            armyAttacker = closestUnit.Pack.Attacker;
            return true;
        }

        private void FinishAttackCycle()
        {
            _isAttacked = false;
            _nextAttackTime = Time.time + Mathf.Max(0f, delayBetweenAttacks);
        }
    }
}
