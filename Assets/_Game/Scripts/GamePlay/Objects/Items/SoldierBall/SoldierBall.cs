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

        public override void Initialize()
        {
            if (_entityType == EntityType.None)
            {
                _entityType = EntityType.FinishTower;
            }

            _hasAppliedBreakBuff = false;

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

        protected override void HandleHealthChange(int current, int max)
        {
            UpdateHealthText(current);

            if (current <= 0)
            {
                ApplyBreakBuff();
                if (effectComponent != null)
                {
                    effectComponent.PlayEffect(EffectType.Break, transform.position + Vector3.up * 1.5f + Vector3.forward * -1.5f);
                }
                DespawnInterval();
                OnBreak();
            }
        }

        private void ApplyBreakBuff()
        {
            if (_hasAppliedBreakBuff || Data == null ||
                Data.ChangeType != SoldierBallData.EChangeType.Increase ||
                (Data.Type != StatType.FireRate && Data.Type != StatType.Damage))
            {
                return;
            }

            _hasAppliedBreakBuff = true;
            GameplayManager.Instance?.ChangeStatModifierData(Data);
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

