using System.Collections.Generic;
using GamePlay.AnimationSystems;
using GamePlay.Characters;
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
        [Header("Spawn Settings")]
        [SerializeField] private Transform slot;
        [SerializeField] private float soldierScale = 1.5f;
        [SerializeField] private float unitHorizontalSpace = 1.2f;

        [Header("Upgrade Settings")]
        [SerializeField] private GameObject upgradeShowObject;

        [Header("Health Settings")]
        [SerializeField] private TextMeshPro healthText;

        [Header("Model Settings")]
        [SerializeField] private SawRotate wheelRotate;
        public bool IsStopMove = true;

        [Header("Effects")]
        [SerializeField] protected EffectComponent effectComponent;
        private readonly List<CharacterUnit> _beltUnits = new List<CharacterUnit>();

        protected void OnDisable()
        {
            ClearBelts();
        }

        public override void Initialize()
        {
            if (_entityType == EntityType.None)
            {
                _entityType = EntityType.FinishTower;
            }

            ClearBelts();

            if (Data != null && Data.ChangeType == SoldierBallData.EChangeType.Increase)
            {
                IsStopMove = false; // [FIX] Cho phép item trôi về sau

                if (upgradeShowObject != null)
                {
                    upgradeShowObject.SetActive(false);
                }

                var characterPrefab = GetCharacterUnitPrefab();
                if (characterPrefab != null)
                {
                    SpawnSoldier(characterPrefab);
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
                // Kill any existing tween on the transform to avoid conflicts
                transform.DOKill();
                transform.localScale = Vector3.one; // Reset to original scale before tweening
                transform.DOPunchScale(Vector3.one * 0.2f, 0.2f, 10, 1f);
                effectComponent?.PlayEffect(EffectType.Break, transform.position + Vector3.up * 1.5f + Vector3.forward * -1.5f);
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

        protected virtual void OnBreak()
        {
        }

        private CharacterUnit GetCharacterUnitPrefab()
        {
            var armySystem = FindObjectOfType<PlayerArmy.PlayerArmySystem>();
            return armySystem != null ? armySystem.CharacterPrefab : null;
        }

        private void SpawnSoldier(CharacterUnit belt)
        {
            if (slot == null || belt == null || Data == null)
            {
                return;
            }

            Vector3 centerPos = slot.position;
            Vector3 rightDir = slot.right;
            Quaternion spawnRotation = Quaternion.Euler(0, 180, 0);

            int amount = Mathf.Max(0, Data.Value);
            float startX = -((amount - 1) * unitHorizontalSpace / 2f);

            int currentLevelIndex = ArmyUpgradeManager.Instance != null ? ArmyUpgradeManager.Instance.CurrentLevel : 0;

            for (int i = 0; i < amount; i++)
            {
                float xOffset = startX + i * unitHorizontalSpace;
                Vector3 pos = centerPos + rightDir * xOffset;

                CharacterUnit unit = belt.Spawn(pos, spawnRotation, slot);
                unit.Transform.localScale = new Vector3(soldierScale, soldierScale, soldierScale);
                unit.PlayAnimation(AnimationType.Idle);

                unit.ApplyVisualLevel(currentLevelIndex);

                _beltUnits.Add(unit);
            }
        }

        private void ClearBelts()
        {
            int innerCount = _beltUnits.Count;
            for (int j = 0; j < innerCount; j++)
            {
                if (_beltUnits[j] != null)
                {
                    _beltUnits[j].Transform.parent = null;
                    _beltUnits[j].Transform.localScale = Vector3.one;
                    _beltUnits[j].Despawn();
                }
            }

            _beltUnits.Clear();
        }

        protected void UpdateHealthText(int health)
        {
            if (healthText == null) return;

            healthText.text = health > 0 ? health.ToString() : "";
        }

        private void SetRotate(bool isRotating)
        {
            wheelRotate?.SetRotating(isRotating);
        }

        protected override void DespawnInterval()
        {
            ClearBelts();

            base.DespawnInterval();
        }
    }
}

