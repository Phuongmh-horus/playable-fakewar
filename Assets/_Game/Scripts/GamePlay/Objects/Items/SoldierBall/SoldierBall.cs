using System.Collections;
using GamePlay.CollisionSystems;
using GamePlay.Entities;
using TMPro;
using UnityEngine;
using GamePlay.ComponentSystems;

namespace GamePlay.Items
{
    public class SoldierBall : StatModifierItem<SoldierBallData>
    {
        [Header("Upgrade Settings")]
        [SerializeField] private GameObject upgradeShowObject;
        [SerializeField, Min(0f)] private float upgradeRiseHeight = 1f;
        [SerializeField, Min(0f)] private float upgradeRiseDuration = 0.18f;
        [SerializeField, Min(0f)] private float upgradeFlyDuration = 0.45f;
        [SerializeField, Min(1f)] private float upgradePeakScale = 1.2f;
        [SerializeField, Min(0f)] private float breakEffectDespawnDelay = 0.7f;

        [Header("Health Settings")]
        [SerializeField] private TextMeshPro healthText;

        [Header("Model Settings")]
        [SerializeField] private SawRotate wheelRotate;
        public bool IsStopMove = true;

        [Header("Hint Arrow")]
        [SerializeField] private GameObject hintArrowVisual;

        [Header("Effects")]
        [SerializeField] protected EffectComponent effectComponent;
        private bool _hasAppliedBreakBuff;
        private bool _breakSequenceStarted;
        private int _nextHitEffectFrame;
        private Transform _upgradeShowTransform;
        private Vector3 _upgradeShowLocalPosition;
        private Vector3 _upgradeShowLocalScale;
        private bool _showHintArrow;

        public bool IsHintArrowVisible => _showHintArrow;

        public void SetHintArrowVisible(bool visible)
        {
            _showHintArrow = visible;
            if (hintArrowVisual == null)
            {
                Transform arrowTransform = transform.Find("Arrow");
                if (arrowTransform != null)
                {
                    hintArrowVisual = arrowTransform.gameObject;
                }
            }

            if (hintArrowVisual != null)
            {
                hintArrowVisual.SetActive(visible);
            }
        }

        public override void Initialize()
        {
            if (_entityType == EntityType.None)
            {
                _entityType = EntityType.FinishTower;
            }

            _hasAppliedBreakBuff = false;
            _breakSequenceStarted = false;
            _nextHitEffectFrame = 0;
            RestoreUpgradeShowVisual();
            SetHintArrowVisible(_showHintArrow);

            base.Initialize();

            if (Pack.Healable != null)
            {
                UpdateHealthText(Pack.Healable.GetCurrentHealth());
            }

            SetRotate(true);
        }

        protected override void HandleWheelCollision()
        {
        }

        protected override void HandleNonWheelCollision(GamePlay.ComponentSystems.IAttacker source)
        {
            base.HandleNonWheelCollision(source);
            TryPlayHitEffect();
        }

        private void TryPlayHitEffect()
        {
            if (Time.frameCount < _nextHitEffectFrame)
            {
                return;
            }

            _nextHitEffectFrame = Time.frameCount + 12;
            effectComponent?.PlayEffect(EffectType.Hit, transform.position + Vector3.up * 2f + Vector3.forward * -3f);
        }

        protected override void HandleHealthChange(int current, int max)
        {
            UpdateHealthText(current);

            if (current <= 0)
            {
                BeginBreakSequence();
            }
        }

        private void BeginBreakSequence()
        {
            if (_breakSequenceStarted)
            {
                return;
            }

            _breakSequenceStarted = true;
            SetHintArrowVisible(false);
            PlayableWaveDefenseEntitySystem.Instance?.Unregister(this);
            if (Pack.Hitable != null)
            {
                CollisionSystem.Unregister(Pack.Hitable);
            }

            effectComponent?.PlayEffect(EffectType.Break, transform.position + Vector3.up * 2f + Vector3.back * 3f);
            bool applied = ApplyBreakBuff();
            if (applied)
            {
                GameplayManager.Instance?.ActiveArmy?.PlaySoldierBallUpgradeEffect();
            }

            StartCoroutine(CoPlayBreakSequence());
        }

        private IEnumerator CoPlayBreakSequence()
        {
            PlayerArmy.PlayerArmySystem army = GameplayManager.Instance != null
                ? GameplayManager.Instance.ActiveArmy
                : null;
            CacheUpgradeShowVisual();

            Vector3 startPosition = _upgradeShowTransform != null
                ? _upgradeShowTransform.position
                : Vector3.zero;
            Vector3 raisedPosition = startPosition + Vector3.up * upgradeRiseHeight;
            float riseDuration = Mathf.Max(0f, upgradeRiseDuration);
            float flyDuration = Mathf.Max(0f, upgradeFlyDuration);
            float visualDuration = riseDuration + flyDuration;
            float despawnDelay = Mathf.Max(0f, breakEffectDespawnDelay);
            float elapsed = 0f;

            while (elapsed < despawnDelay)
            {
                elapsed += Time.deltaTime;
                if (_upgradeShowTransform != null && army != null)
                {
                    if (elapsed < riseDuration && riseDuration > 0f)
                    {
                        float progress = elapsed / riseDuration;
                        _upgradeShowTransform.position = Vector3.Lerp(startPosition, raisedPosition, progress);
                        _upgradeShowTransform.localScale = Vector3.Lerp(
                            _upgradeShowLocalScale,
                            _upgradeShowLocalScale * upgradePeakScale,
                            progress);
                    }
                    else if (elapsed < visualDuration && flyDuration > 0f)
                    {
                        float progress = (elapsed - riseDuration) / flyDuration;
                        _upgradeShowTransform.position = Vector3.Lerp(raisedPosition, army.UpgradeEffectPosition, progress);
                        _upgradeShowTransform.localScale = Vector3.Lerp(
                            _upgradeShowLocalScale * upgradePeakScale,
                            Vector3.zero,
                            progress);
                    }
                    else if (elapsed >= visualDuration)
                    {
                        upgradeShowObject.SetActive(false);
                    }
                }

                yield return null;
            }

            DespawnInterval();
            OnBreak();
        }

        private bool ApplyBreakBuff()
        {
            if (_hasAppliedBreakBuff || Data == null || Data.Type == StatType.None)
            {
                return false;
            }

            _hasAppliedBreakBuff = true;
            GameplayManager manager = GameplayManager.Instance;
            if (manager == null)
            {
                return false;
            }

            manager.ChangeStatModifierData(Data);
            return true;
        }

        private void CacheUpgradeShowVisual()
        {
            if (_upgradeShowTransform != null || upgradeShowObject == null)
            {
                return;
            }

            _upgradeShowTransform = upgradeShowObject.transform;
            _upgradeShowLocalPosition = _upgradeShowTransform.localPosition;
            _upgradeShowLocalScale = _upgradeShowTransform.localScale;
        }

        private void RestoreUpgradeShowVisual()
        {
            CacheUpgradeShowVisual();
            if (_upgradeShowTransform == null)
            {
                return;
            }

            _upgradeShowTransform.localPosition = _upgradeShowLocalPosition;
            _upgradeShowTransform.localScale = _upgradeShowLocalScale;
            if (upgradeShowObject != null)
            {
                upgradeShowObject.SetActive(true);
            }
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
