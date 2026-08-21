using GamePlay.AnimationSystems;
using GamePlay.CombatSystems;
using GamePlay.Entities;
using GamePlay.Effects;
using GamePlay.Items;
using UnityEngine;

namespace GamePlay.Items
{
    public class WallBreakable : SoldierBall
    {
        public CanAttackRangeModifier breakableConfig;

        [SerializeField] protected float breakAnimationWaitTime = 0.3f;

        // [SerializeField] protected bool isUserBreakObject = false;
        // [SerializeField] protected GameObject gameObjectToEnable;
        // [SerializeField] protected GameObject gameObjectToDisable;
        [SerializeField] protected float delayInterval = 3f;
        [SerializeField] private NoProjectileFireZone noProjectileFireZone;

        public bool IsBreaked => isBreaked;
        public bool BlocksMovingGates => blocksMovingGates && !isBreaked && !isBreaking && isActiveAndEnabled;

        [Header("Moving Gate Block")]
        [SerializeField] private bool blocksMovingGates = true;
        [SerializeField, Min(0f)] private float movingGateBlockHalfWidth = 2.25f;
        [SerializeField, Min(0f)] private float movingGateStopDistance = 0.45f;

        protected bool isBreaked;
        protected bool isBreaking;

        public float MovingGateBlockHalfWidth => movingGateBlockHalfWidth;
        public float MovingGateStopDistance => movingGateStopDistance;

        public override void Initialize()
        {
            if (_entityType == EntityType.None || _entityType == EntityType.FinishTower)
            {
                _entityType = EntityType.Obstacle;
            }

            if (noProjectileFireZone == null)
            {
                noProjectileFireZone = GetComponentInChildren<NoProjectileFireZone>(true);
            }

            isBreaked = false;
            isBreaking = false;
            noProjectileFireZone?.Deactivate();
            base.Initialize();
        }

        protected override void HandleHealthChange(int current, int max)
        {
            base.HandleHealthChange(current, max);

            if (current <= 0)
            {
                HandleBreakableDestroyed();
            }
        }

        protected override void OnBreak()
        {
            if (isBreaked || isBreaking) return;

            isBreaking = true;
            RegisterEvents(false);

            if ((ActiveFlags & CapabilityFlags.Animator) != 0 && Pack.Animator != null)
            {
                Pack.Animator.PlayAnimation(AnimationType.Break, breakAnimationWaitTime, CompleteBreakSequence);
                return;
            }

            CompleteBreakSequence();
        }

        protected void HandleBreakableDestroyed()
        {
            if (isBreaked || isBreaking) return;

            Data.Type = StatType.Character;
            GameplayManager.Instance?.ChangeStatModifierData(Data);

            OnBreak();
        }

        protected virtual void CompleteBreakSequence()
        {
            isBreaked = true;
            isBreaking = false;
            base.OnBreak();
            noProjectileFireZone?.Activate();
            breakableConfig?.ApplyBonus();

            if (SoundManager.Instance != null)
            {
                SoundManager.Instance.PlayOneShot(AudioClipName.SFX_CharacterAttack);
            }

            // if (isUserBreakObject)
            // {
            //     // User breakable: enable/disable specific objects, no despawn
            //     if (gameObjectToEnable != null)
            //         gameObjectToEnable.SetActive(true);
            //     if (gameObjectToDisable != null)
            //         gameObjectToDisable.SetActive(false);

            //     Invoke(nameof(DespawnInterval), delayInterval);
            // }
            // else
            // {
            // Normal breakable: despawn after break
            DespawnInterval();
            // }
        }
    }
}
