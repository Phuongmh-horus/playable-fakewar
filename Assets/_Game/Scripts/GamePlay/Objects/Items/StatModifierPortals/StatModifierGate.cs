using System.Collections;
using System.Collections.Generic;
using GamePlay.CombatSystems;
using GamePlay.CollisionSystems;
using GamePlay.ComponentSystems;
using GamePlay.OscillationSystems;
using TMPro;
using UnityEngine;
using DG.Tweening;

namespace GamePlay.Items
{
    public class StatModifierGate : StatModifierItem<StatModifierGateData>
    {
        private static readonly int FillAmountProp = Shader.PropertyToID("_FillAmount");
        private const int GateRenderQueue = 3100;
        private const int GateTextRenderQueue = 3101;
        private const string GateShaderName = "Playable/Gate Glow Instanced";

        private static Material _sharedGateMaterial;
        private static Material _sharedGateTextMaterial;

        [Header("Display Settings")]
        [SerializeField] protected TextMeshPro valueText;
        [SerializeField] private HitComponent hitComponent;

        [Header("Color Settings")]
        [SerializeField] protected MeshRenderer gateRenderer;
        [SerializeField] protected Color increaseColor = Color.cyan;
        [SerializeField] protected Color decreaseColor = Color.red;
        private MaterialPropertyBlock _propBlock;
        private MaterialPropertyBlock _progressMpb;

        [Header("Armor Visual Settings")]
        [SerializeField] protected Transform armorParent;
        [SerializeField] protected float groundYOffset = 0f;
        [SerializeField] protected SpriteRenderer progressSprite;
        [SerializeField] private GameObject hpBar;

        [Header("Drop Physics Config")]
        [SerializeField] protected float throwForce = 3f;
        [SerializeField] protected float throwHeight = 2f;
        [SerializeField] protected float throwDuration = 0.5f;

        [Header("Bounce Config")]
        [SerializeField] protected float bounceHeight = 0.5f;
        [SerializeField] protected float bounceDuration = 0.3f;

        [Header("Sound Effects")]
        [SerializeField] private AudioClipName hitByWheelSound = AudioClipName.None;

        [Header("Hit Scale Pulse")]
        [SerializeField] private float scaleUp = 1.08f;
        [SerializeField] private float scaleUpDuration = 0.08f;
        [SerializeField] private float scaleDownDuration = 0.15f;

        [Header("Hit Bend")]
        [SerializeField] private float bendAngle = 12f;
        [SerializeField] private float bendDuration = 0.08f;
        [SerializeField] private float returnDuration = 0.15f;

        [Header("Oscillation")]
        [SerializeField] private bool onlyCenterOscillates = true;
        [SerializeField] private float centerXThreshold = 0.1f;

        private readonly List<Transform> _armorParts = new List<Transform>();
        private int _maxArmor;
        private int _currentActiveParts;
        private float _armorPerPart = 1f;

        private Vector3 _originalScale;
        private Quaternion _baseRotation;
        private Coroutine _scalePulseRoutine;
        private Sequence _bendSequence;
        private bool _isCollectedByArmy;
        private HitTextFlyEffect _flyTextEffect;
        private float _nextBendFeedbackTime;
        private float _nextScalePulseTime;
        private float _nextAudioTime;

        private static void PreparePropertyBlock(Renderer renderer, MaterialPropertyBlock block)
        {
            if (block == null) return;
            block.Clear();
        }

        protected override void Awake()
        {
            base.Awake();
            SetupArmorParts();
            _progressMpb = new MaterialPropertyBlock();
            ConfigureRenderGrouping();

            // Ensure EntityType is MovingGate at runtime (for collision masks)
            if (_entityType == Entities.EntityType.None)
            {
                _entityType = Entities.EntityType.MovingGate;
            }
            _flyTextEffect = GetComponent<HitTextFlyEffect>();
        }

        private void ConfigureRenderGrouping()
        {
            if (gateRenderer != null && gateRenderer.sharedMaterial != null)
            {
                if (_sharedGateMaterial == null)
                {
                    var gateShader = Shader.Find(GateShaderName);
                    if (gateShader != null)
                    {
                        _sharedGateMaterial = new Material(gateRenderer.sharedMaterial)
                        {
                            name = "GateGlowInstanced (Shared Runtime)",
                            shader = gateShader,
                            renderQueue = GateRenderQueue,
                            enableInstancing = true,
                            hideFlags = HideFlags.DontSave
                        };
                    }
                }

                if (_sharedGateMaterial != null)
                    gateRenderer.sharedMaterial = _sharedGateMaterial;
            }

            var texts = GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                var text = texts[i];
                if (text == null || text.fontSharedMaterial == null) continue;

                if (_sharedGateTextMaterial == null)
                {
                    _sharedGateTextMaterial = new Material(text.fontSharedMaterial)
                    {
                        name = "GateCounterText (Shared Runtime)",
                        renderQueue = GateTextRenderQueue,
                        hideFlags = HideFlags.DontSave
                    };
                }

                text.isOverlay = false;
                text.fontSharedMaterial = _sharedGateTextMaterial;

                var textRenderer = text.GetComponent<Renderer>();
                if (textRenderer != null)
                    textRenderer.sortingOrder = 0;
            }
        }

        private void SetupArmorParts()
        {
            _armorParts.Clear();
            if (armorParent == null) return;

            foreach (Transform child in armorParent)
                _armorParts.Add(child);
        }

        public override void Initialize()
        {
            _isCollectedByArmy = false;
            base.Initialize();

            _originalScale = transform.localScale;
            _baseRotation = transform.localRotation;

            // Ensure HitComponent is the registered IHitable for accurate collisions.
            var hitComp = hitComponent;
            if (hitComp != null)
            {
                hitComp.Initialize();

                // [FIX] Double-Subscription Check
                // ItemUnit.Initialize already finds HitComponent and registers everything.
                // Only do this if ItemUnit MISSED it or picked the wrong one.
                bool alreadyCorrect = (Pack.Hitable != null && ReferenceEquals(Pack.Hitable, hitComp));

                if (!alreadyCorrect)
                {
                    if (Pack.Hitable != null)
                    {
                        RegisterEvents(false);
                        CollisionSystem.Unregister(Pack.Hitable);
                    }

                    Pack.Hitable = hitComp;
                    ActiveFlags |= CapabilityFlags.Hit;
                    CollisionSystem.Register(hitComp, hitComp.transform);
                    RegisterEvents(true);
                }
            }
            else
            {
                Debug.LogWarning($"[StatModifierGate] Missing HitComponent on {name}. Assign in Inspector.");
            }

            UpdateGateColor();
            UpdateArmor();
            UpdateText();
            UpdateImage();

            DisableOscillationIfSide();
        }

        private void DisableOscillationIfSide()
        {
            if (!onlyCenterOscillates) return;
            if ((ActiveFlags & CapabilityFlags.Oscillate) == 0 || Pack.Oscillator == null) return;

            if (Mathf.Abs(Transform.position.x) > centerXThreshold)
            {
                OscillationSystem.Unregister(Pack.Oscillator);
            }
        }

        protected override void AdjustStatModifierValue(int value = 0)
        {
            int previousArmor = Data.Armor;

            base.AdjustStatModifierValue(value);

            if (Data.Armor != previousArmor && _armorParts.Count > 0)
                HandleArmorVisuals();

            UpdateGateColor();
            UpdateText();
            UpdateImage();
        }

        protected override void HandleWheelCollision()
        {
            base.HandleWheelCollision();

            if (_flyTextEffect != null && Data != null)
            {
                _flyTextEffect.ShowCustomText("+" + Data.Value.ToString(), Color.yellow);
            }

            if (Time.time >= _nextScalePulseTime)
            {
                _nextScalePulseTime = Time.time + 0.1f;
                PlayScalePulse();
            }

            if (Time.time >= _nextAudioTime)
            {
                _nextAudioTime = Time.time + 0.1f;
                if (SoundManager.Instance != null)
                    SoundManager.Instance.PlayOneShot(hitByWheelSound);
            }

            Pack.Effector?.PlayEffect(EffectType.Land);
        }

        protected override void HandleNonWheelCollision(IAttacker source)
        {
            if (source != null && source.EntityType == Entities.EntityType.Character)
            {
                CollectByArmy();
                return;
            }

            if (source != null && source.EntityType != Entities.EntityType.Character && source.EntityType != Entities.EntityType.Wheel)
            {
                if (Data.Type == StatType.Character)
                {
                    return; // FireSoldier ko nhan damage tu dan
                }
                if (Data.Armor <= 0)
                {
                    return; // Gate da vo giap ko nhan them damage tu dan
                }
            }

            base.HandleNonWheelCollision(source);
            PlayBend();
        }

        protected override void HandleHealthChange(int current, int max)
        {
            // Do not despawn when health reaches 0. Gates should only be collected by the Army.
            // Armor damage is handled via AdjustStatModifierValue.
        }

        public void CollectByArmy()
        {
            if (_isCollectedByArmy)
            {
                return;
            }

            _isCollectedByArmy = true;

            if (Data != null)
            {
                var copyData = new StatModifierGateData
                {
                    Type = Data.Type,
                    Value = (Data.Type == StatType.FireRate || Data.Type == StatType.FireRange) ? Data.Value : Data.Value,
                    Armor = Data.Armor
                };
                GameplayManager.Instance?.ChangeStatModifierData(copyData);

            }
            if (_flyTextEffect != null && Data != null)
            {
                _flyTextEffect.ShowCustomText("+" + Data.Value.ToString(), Color.yellow);
            }
            Pack.Effector?.PlayEffect(EffectType.Land);
            DespawnInterval();
        }

        private void PlayScalePulse()
        {
            if (!isActiveAndEnabled) return;
            if (_scalePulseRoutine != null) return;

            _scalePulseRoutine = StartCoroutine(CoScalePulse());
        }

        private IEnumerator CoScalePulse()
        {
            if (_originalScale == Vector3.zero)
            {
                _originalScale = transform.localScale;
            }

            Vector3 from = _originalScale;
            Vector3 to = _originalScale * scaleUp;

            float t = 0f;
            while (t < scaleUpDuration)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / Mathf.Max(0.001f, scaleUpDuration));
                transform.localScale = Vector3.Lerp(from, to, k);
                yield return null;
            }

            t = 0f;
            while (t < scaleDownDuration)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / Mathf.Max(0.001f, scaleDownDuration));
                transform.localScale = Vector3.Lerp(to, _originalScale, k);
                yield return null;
            }

            transform.localScale = _originalScale;
            _scalePulseRoutine = null;
        }

        private void PlayBend()
        {
            if (!isActiveAndEnabled) return;
            if (Time.time < _nextBendFeedbackTime) return;

            _nextBendFeedbackTime = Time.time + 0.1f;

            KillBendSequence();
            transform.localRotation = _baseRotation;
            Quaternion toRot = _baseRotation * Quaternion.Euler(-bendAngle, 0f, 0f);

            _bendSequence = DOTween.Sequence();
            _bendSequence.SetId(this);
            _bendSequence.Append(transform.DOLocalRotateQuaternion(toRot, bendDuration).SetEase(Ease.OutQuad));
            _bendSequence.Append(transform.DOLocalRotateQuaternion(_baseRotation, returnDuration).SetEase(Ease.InQuad));
        }

        private void StopScalePulse()
        {
            if (_scalePulseRoutine != null)
            {
                StopCoroutine(_scalePulseRoutine);
                _scalePulseRoutine = null;
            }

            if (transform != null && _originalScale != Vector3.zero)
            {
                transform.localScale = _originalScale;
            }
        }

        private void KillBendSequence()
        {
            if (_bendSequence != null)
            {
                _bendSequence.Kill(true);
                _bendSequence = null;
            }
        }

        private void OnEnable()
        {
            _isCollectedByArmy = false;
            transform.localScale = Vector3.one;
        }

        private void OnDisable()
        {
            StopScalePulse();
            KillBendSequence();
        }

        private bool _isDespawning = false;

        protected override void DespawnInterval()
        {
            if (_isDespawning) return;
            _isDespawning = true;

            StopScalePulse();
            KillBendSequence();

            if (Pack.Hitable != null)
            {
                CollisionSystem.Unregister(Pack.Hitable);
            }

            StartCoroutine(ScaleDownRoutine());
        }

        private IEnumerator ScaleDownRoutine()
        {
            float elapsed = 0f;
            float duration = 0.25f;
            Vector3 startScale = transform.localScale;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                transform.localScale = Vector3.Lerp(startScale, Vector3.zero, elapsed / duration);
                yield return null;
            }

            transform.localScale = Vector3.zero;
            _isDespawning = false;
            base.DespawnInterval();
        }

        private void HandleArmorVisuals()
        {
            if (_armorPerPart <= 0f) _armorPerPart = 1f;

            int neededParts = Mathf.CeilToInt(Data.Armor / _armorPerPart);
            neededParts = Mathf.Clamp(neededParts, 0, _armorParts.Count);

            while (_currentActiveParts > neededParts)
            {
                int partIndex = _currentActiveParts - 1;
                Transform partToDrop = _armorParts[partIndex];

                if (partToDrop != null && partToDrop.gameObject.activeSelf)
                    DropArmorPart(partToDrop);

                _currentActiveParts--;
            }
        }

        private void DropArmorPart(Transform part)
        {
            part.SetParent(null);

            Vector3 startPos = part.position;
            Vector3 backwardDir = -transform.forward;

            float randomAngle = Random.Range(-20f, 20f);
            Vector3 throwDir = Quaternion.Euler(0, randomAngle, 0) * backwardDir;

            Vector3 landPos = startPos + (throwDir * throwForce);
            float targetGroundY = transform.position.y + groundYOffset;
            landPos.y = targetGroundY;

            Quaternion startRot = part.rotation;
            Quaternion landRot = Quaternion.Euler(-90f, Random.Range(0f, 360f), 0f);

            PlayThrowThenBounce(part, startPos, landPos, startRot, landRot, throwDir, targetGroundY);
        }

        private void PlayThrowThenBounce(
            Transform part,
            Vector3 startPos,
            Vector3 landPos,
            Quaternion startRot,
            Quaternion landRot,
            Vector3 throwDir,
            float groundY)
        {
            Vector3 finalRestPos = landPos + (throwDir.normalized * 0.5f);
            finalRestPos.y = groundY;

            Sequence seq = DOTween.Sequence();
            seq.Append(part.DOJump(landPos, throwHeight, 1, throwDuration).SetEase(Ease.Linear));
            seq.Join(part.DORotateQuaternion(landRot, throwDuration).SetEase(Ease.Linear));
            seq.Append(part.DOJump(finalRestPos, bounceHeight, 1, bounceDuration).SetEase(Ease.OutQuad));
            seq.OnComplete(() => part.gameObject.SetActive(false));
            seq.SetId(part);
        }

        private void UpdateGateColor()
        {
            if (gateRenderer == null || gateRenderer.sharedMaterials == null || gateRenderer.sharedMaterials.Length == 0) return;

            if (_propBlock == null) _propBlock = new MaterialPropertyBlock();

            try
            {
                PreparePropertyBlock(gateRenderer, _propBlock);
                Color targetColor = Data.Value > 0 ? increaseColor : decreaseColor;
                _propBlock.SetColor("_Color", targetColor);
                gateRenderer.SetPropertyBlock(_propBlock);
            }
            catch { }
        }

        private void UpdateArmor()
        {
            if (Data.Armor <= 0)
            {
                foreach (var part in _armorParts)
                    if (part != null) part.gameObject.SetActive(false);

                _maxArmor = 0;
                _currentActiveParts = 0;
                _armorPerPart = 1f;
                return;
            }

            foreach (var part in _armorParts)
                if (part != null) part.gameObject.SetActive(true);

            _maxArmor = Data.Armor;
            _currentActiveParts = _armorParts.Count;
            _armorPerPart = _armorParts.Count > 0 ? (float)_maxArmor / _armorParts.Count : 1f;
            if (_armorPerPart <= 0f) _armorPerPart = 1f;
        }

        private void UpdateText()
        {
            if (valueText != null)
                valueText.text = "+ " + Data.Value.ToString();
        }

        private void UpdateImage()
        {
            if (progressSprite == null) return;

            if (Data.Armor <= 0)
            {
                if (hpBar != null) hpBar.SetActive(false);
                return;
            }

            if (hpBar != null) hpBar.SetActive(true);

            float armorPercent = _maxArmor > 0 ? (float)Data.Armor / _maxArmor : 0f;

            float min = 0.532f;
            float max = 0.792f;
            float fillAmount = Mathf.Lerp(min, max, armorPercent);

            if (progressSprite.sharedMaterials == null || progressSprite.sharedMaterials.Length == 0) return;

            if (_progressMpb == null) _progressMpb = new MaterialPropertyBlock();

            try
            {
                PreparePropertyBlock(progressSprite, _progressMpb);
                _progressMpb.SetFloat(FillAmountProp, fillAmount);
                progressSprite.SetPropertyBlock(_progressMpb);
            }
            catch { }
        }



        private static Transform FindChildContains(Transform root, string contains)
        {
            if (root == null) return null;

            // BFS đơn giản
            var q = new Queue<Transform>();
            q.Enqueue(root);

            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                if (cur.name.Contains(contains)) return cur;

                for (int i = 0; i < cur.childCount; i++)
                    q.Enqueue(cur.GetChild(i));
            }

            return null;
        }
    }
}

