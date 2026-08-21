using GamePlay.Entities;
using Pools;
using TMPro;
using UnityEngine;
using GamePlay.ComponentSystems;
using DG.Tweening;

namespace GamePlay.Items
{
    public class SoldierBall : StatModifierItem<SoldierBallData>
    {
        [Header("Upgrade Settings")]
        [SerializeField] private GameObject upgradeShowObject;

        [Header("Health Settings")]
        [SerializeField] private TextMeshPro healthText;

        [Header("Model Settings")]
        [SerializeField] private SawRotate wheelRotate;
        public bool IsStopMove = true;

        [Header("Effects")]
        [SerializeField] protected EffectComponent effectComponent;
        [SerializeField, Min(0f)] private float hitFeedbackInterval = 0.3f;
        private float _nextHitFeedbackTime;

        public override void Initialize()
        {
            if (_entityType == EntityType.None)
            {
                _entityType = EntityType.FinishTower;
            }

            _nextHitFeedbackTime = 0f;

            if (Data != null && Data.ChangeType == SoldierBallData.EChangeType.Increase)
            {

                if (upgradeShowObject != null)
                {
                    upgradeShowObject.SetActive(false);
                }

            }
            else if (Data != null && Data.ChangeType == SoldierBallData.EChangeType.Upgrade)
            {
                if (upgradeShowObject != null)
                {
                    upgradeShowObject.SetActive(true);
                }
            }

            base.Initialize();

            if (Pack.Healable != null)
            {
                UpdateHealthText(Pack.Healable.GetCurrentHealth());
            }

            SetRotate(false);
        }

        protected override void HandleWheelCollision()
        {
            if (Data != null && Data.ChangeType == SoldierBallData.EChangeType.Increase)
            {
                GameplayManager.Instance?.ChangeStatModifierData(Data);
                // Not using it now, save for another scenario
                // if (effectComponent != null)
                // {
                //     effectComponent.PlayEffect(EffectType.Break); 
                // }
                DespawnInterval();
                OnBreak();
            }
        }

        protected override void HandleHealthChange(int current, int max)
        {
            UpdateHealthText(current);

            // [FIX] Scale pulse effect on hit
            if (current > 0 && current < max)
            {
                PlayHitFeedback();
            }

            if (current <= 0)
            {
                if (Data != null && Data.ChangeType == SoldierBallData.EChangeType.Upgrade)
                {
                    if (effectComponent != null)
                        effectComponent.PlayEffect(EffectType.Break, transform.position + Vector3.up * 1.5f + Vector3.forward * -1.5f);
                }
                GameplayManager.Instance?.ChangeStatModifierData(Data);
                DespawnInterval();
                OnBreak();
            }
        }

        private void PlayHitFeedback()
        {
            if (Time.time < _nextHitFeedbackTime)
            {
                return;
            }

            _nextHitFeedbackTime = Time.time + hitFeedbackInterval;
            transform.DOKill();
            transform.localScale = Vector3.one;
            transform.DOPunchScale(Vector3.one * 0.2f, 0.2f, 10, 1f);
            effectComponent?.PlayEffect(EffectType.Break, transform.position + Vector3.up * 1.5f + Vector3.forward * -1.5f);
        }

        protected virtual void OnBreak()
        {
        }



        protected void UpdateHealthText(int health)
        {
            if (healthText == null) return;

            healthText.SetText(health > 0 ? "{0}" : string.Empty, health);
        }

        private void SetRotate(bool isRotating)
        {
            wheelRotate?.SetRotating(isRotating);
        }

        protected override void DespawnInterval()
        {
            base.DespawnInterval();
        }
    }
}

