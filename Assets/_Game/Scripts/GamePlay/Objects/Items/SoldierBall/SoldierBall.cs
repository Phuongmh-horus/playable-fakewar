using GamePlay.Entities;
using System.Collections.Generic;
using Pools;
using TMPro;
using UnityEngine;
using GamePlay.ComponentSystems;

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
        private float _hitFeedbackEndTime;
        private const float HitFeedbackDuration = 0.3f;

        public static readonly List<SoldierBall> ActiveBalls = new List<SoldierBall>();

        private void OnEnable()
        {
            ActiveBalls.Add(this);
        }

        private void OnDisable()
        {
            ActiveBalls.Remove(this);
        }

        public void Tick()
        {
            if (_hitFeedbackEndTime <= 0f)
            {
                return;
            }

            float elapsed = HitFeedbackDuration - (_hitFeedbackEndTime - Time.time);
            if (elapsed >= HitFeedbackDuration)
            {
                transform.localScale = Vector3.one;
                _hitFeedbackEndTime = 0f;
                return;
            }

            float pulse = Mathf.Sin(Mathf.Clamp01(elapsed / HitFeedbackDuration) * Mathf.PI);
            transform.localScale = Vector3.one * (1f + pulse * 0.15f);
        }

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
            transform.localScale = Vector3.one;
            _hitFeedbackEndTime = Time.time + HitFeedbackDuration;
            effectComponent?.PlayEffect(EffectType.Break, transform.position + Vector3.up * 1.5f + Vector3.forward * -1.5f);
        }

        protected virtual void OnBreak()
        {
        }



        private int _lastHealthTextValue = -1;

        protected void UpdateHealthText(int health)
        {
            if (healthText == null || _lastHealthTextValue == health) return;
            
            _lastHealthTextValue = health;
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

