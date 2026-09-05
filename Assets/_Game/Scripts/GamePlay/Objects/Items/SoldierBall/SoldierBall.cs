using GamePlay.Entities;
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
        private bool _hasAppliedBreakBuff;
        private int _nextBreakEffectFrame;

        public override void Initialize()
        {
            if (_entityType == EntityType.None)
            {
                _entityType = EntityType.FinishTower;
            }

            _hasAppliedBreakBuff = false;
            _nextBreakEffectFrame = 0;

            if (upgradeShowObject != null)
            {
                upgradeShowObject.SetActive(true);
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
        }

        protected override void HandleNonWheelCollision(GamePlay.ComponentSystems.IAttacker source)
        {
            base.HandleNonWheelCollision(source);
            TryPlayBreakEffect();
        }

        private void TryPlayBreakEffect()
        {
            if (Time.frameCount < _nextBreakEffectFrame)
            {
                return;
            }

            _nextBreakEffectFrame = Time.frameCount + 12;
            effectComponent?.PlayEffect(EffectType.Break, transform.position + Vector3.up * 1.5f + Vector3.forward * -2f);
        }

        protected override void HandleHealthChange(int current, int max)
        {
            UpdateHealthText(current);

            if (current <= 0)
            {
                ApplyBreakBuff();
                DespawnInterval();
                OnBreak();
            }
        }

        private void ApplyBreakBuff()
        {
            if (_hasAppliedBreakBuff || Data == null || Data.Type == StatType.None)
            {
                return;
            }

            _hasAppliedBreakBuff = true;
            GameplayManager manager = GameplayManager.Instance;
            if (manager == null)
            {
                return;
            }

            manager.ChangeStatModifierData(Data);
            manager.RunUpgradeEffect();
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

