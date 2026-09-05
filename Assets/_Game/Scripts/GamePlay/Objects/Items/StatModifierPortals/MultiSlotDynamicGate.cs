using UnityEngine;

namespace GamePlay.Items
{
    [DisallowMultipleComponent]
    public sealed class MultiSlotDynamicGate : ItemUnit
    {
        private const float WidthEpsilon = 0.0001f;

        [Header("Slots")]
        [SerializeField] private StatModifierGate[] slots = new StatModifierGate[3];

        [Header("Width Percent Defaults")]
        [SerializeField, Min(0.01f)] private float defaultWidthGrowPercent = 4f;
        [SerializeField] private float defaultMinimumWidthPercent = 10f;
        [SerializeField, Min(0f), Tooltip("0 = derive from the authored slot positions and hit widths.")]
        private float totalWidth;

        private readonly float[] _damageTotals = new float[3];
        private readonly float[] _widthPercents = new float[3];
        private readonly float[] _referenceWidths = new float[3];
        private int _slotCount;
        private int _activeSlotIndex = -1;
        private bool _slotsInitialized;
        private bool _layoutInitialized;
        private bool _isCollectedByArmy;

        protected override void Awake()
        {
            base.Awake();
            InitializeSlots();
        }

        public float DefaultWidthGrowPercent => defaultWidthGrowPercent;
        public float DefaultMinimumWidthPercent => defaultMinimumWidthPercent;
        public float TotalWidth => totalWidth;
        private float EffectiveMinimumWidthPercent => Mathf.Min(
            defaultMinimumWidthPercent,
            100f / Mathf.Max(1, _slotCount));

        public int SlotCount
        {
            get
            {
                InitializeSlots();
                return _slotCount;
            }
        }

        public StatModifierGate GetSlot(int index)
        {
            InitializeSlots();
            return index >= 0 && index < _slotCount ? slots[index] : null;
        }

        public void ApplyContentOverride(
            float configuredWidthGrowPercent,
            float configuredMinimumWidthPercent,
            float configuredTotalWidth,
            MultiSlotGateSlotOverride[] slotOverrides)
        {
            InitializeSlots();
            defaultWidthGrowPercent = Mathf.Max(0.01f, configuredWidthGrowPercent);
            defaultMinimumWidthPercent = Mathf.Clamp(configuredMinimumWidthPercent, 0f, 100f);
            totalWidth = Mathf.Max(0f, configuredTotalWidth);

            if (slotOverrides == null)
            {
                return;
            }

            int count = Mathf.Min(_slotCount, slotOverrides.Length);
            for (int i = 0; i < count; i++)
            {
                MultiSlotGateSlotOverride slotOverride = slotOverrides[i];
                StatModifierGate slot = slots[i];
                if (slot == null || slot.Data == null || slotOverride == null || !slotOverride.overrideSlot)
                {
                    continue;
                }

                slot.Data.Operation = slotOverride.operation;
                slot.Data.Value = slotOverride.value;
                slot.Data.Multiplier = slotOverride.multiplier;
                slot.Data.Armor = slotOverride.armor;

                var health = slot.GetComponentInChildren<GamePlay.HealthSystems.HealthComponent>(true);
                if (health != null && slotOverride.maxHealth > 0)
                {
                    health.SetMaxHealth(slotOverride.maxHealth, refill: true);
                }
            }
        }

        public override void Initialize()
        {
            InitializeSlots();
            _activeSlotIndex = -1;
            _layoutInitialized = false;
            _isCollectedByArmy = false;
            for (int i = 0; i < _slotCount; i++)
            {
                _damageTotals[i] = 0f;
                _widthPercents[i] = 0f;
            }

            InitializeLayout();
            for (int i = 0; i < _slotCount; i++)
            {
                slots[i]?.Initialize();
            }
        }

        public bool TryCollectByArmy(StatModifierGate slot)
        {
            InitializeSlots();
            if (GetSlotIndex(slot) < 0)
            {
                return false;
            }

            if (_isCollectedByArmy)
            {
                return true;
            }

            _isCollectedByArmy = true;
            slot.ApplyArmyCollection();

            for (int i = 0; i < _slotCount; i++)
            {
                slots[i]?.ReleaseGeneratedContent();
            }

            ReleaseGeneratedContent();
            return true;
        }

        public void RegisterSlotDamage(StatModifierGate slot, int damage)
        {
            if (slot == null || damage <= 0)
            {
                return;
            }

            InitializeSlots();
            int slotIndex = GetSlotIndex(slot);
            if (slotIndex < 0)
            {
                return;
            }

            _damageTotals[slotIndex] += damage;
            if (_activeSlotIndex < 0 || _damageTotals[slotIndex] > _damageTotals[_activeSlotIndex])
            {
                _activeSlotIndex = slotIndex;
            }
        }

        public bool TryExpandActiveSlot(StatModifierGate slot)
        {
            InitializeLayout();
            int slotIndex = GetSlotIndex(slot);
            if (slotIndex < 0 || slotIndex != _activeSlotIndex || _slotCount < 2)
            {
                return false;
            }

            float transfer = Mathf.Min(defaultWidthGrowPercent, GetPassiveRoom(slotIndex));
            if (transfer <= WidthEpsilon)
            {
                return false;
            }

            float remainingTransfer = transfer;
            for (int pass = 0; pass < _slotCount && remainingTransfer > WidthEpsilon; pass++)
            {
                int shrinkableCount = 0;
                for (int i = 0; i < _slotCount; i++)
                {
                    if (i != slotIndex && _widthPercents[i] > EffectiveMinimumWidthPercent + WidthEpsilon)
                    {
                        shrinkableCount++;
                    }
                }

                if (shrinkableCount == 0)
                {
                    break;
                }

                float share = remainingTransfer / shrinkableCount;
                float reducedThisPass = 0f;
                for (int i = 0; i < _slotCount; i++)
                {
                    if (i == slotIndex)
                    {
                        continue;
                    }

                    float reduction = Mathf.Min(share, Mathf.Max(0f, _widthPercents[i] - EffectiveMinimumWidthPercent));
                    if (reduction <= 0f)
                    {
                        continue;
                    }

                    _widthPercents[i] -= reduction;
                    reducedThisPass += reduction;
                }

                if (reducedThisPass <= WidthEpsilon)
                {
                    break;
                }

                remainingTransfer -= reducedThisPass;
            }

            float actualTransfer = transfer - remainingTransfer;
            if (actualTransfer <= WidthEpsilon)
            {
                return false;
            }

            _widthPercents[slotIndex] += actualTransfer;
            RebuildGateGeometry();
            return true;
        }

        private float GetPassiveRoom(int activeSlotIndex)
        {
            float room = 0f;
            for (int i = 0; i < _slotCount; i++)
            {
                if (i != activeSlotIndex)
                {
                    room += Mathf.Max(0f, _widthPercents[i] - EffectiveMinimumWidthPercent);
                }
            }
            return room;
        }

        private void InitializeLayout()
        {
            if (_layoutInitialized)
            {
                return;
            }

            InitializeSlots();
            if (_slotCount == 0)
            {
                return;
            }

            if (totalWidth <= WidthEpsilon)
            {
                float minX = float.PositiveInfinity;
                float maxX = float.NegativeInfinity;
                for (int i = 0; i < _slotCount; i++)
                {
                    var hit = slots[i].GetComponent<GamePlay.ComponentSystems.HitComponent>();
                    float width = hit != null ? Mathf.Max(0.01f, hit.colliderSize.x) : 1f;
                    _referenceWidths[i] = width;
                    float center = slots[i].transform.localPosition.x;
                    minX = Mathf.Min(minX, center - width * 0.5f);
                    maxX = Mathf.Max(maxX, center + width * 0.5f);
                }
                totalWidth = Mathf.Max(0.01f, maxX - minX);
            }

            for (int i = 0; i < _slotCount; i++)
            {
                var hit = slots[i].GetComponent<GamePlay.ComponentSystems.HitComponent>();
                if (_referenceWidths[i] <= 0f)
                {
                    _referenceWidths[i] = hit != null ? Mathf.Max(0.01f, hit.colliderSize.x) : totalWidth / _slotCount;
                }
            }

            float normalizedMinimumTotal = EffectiveMinimumWidthPercent * _slotCount;
            float extraPerSlot = (100f - normalizedMinimumTotal) / _slotCount;
            for (int i = 0; i < _slotCount; i++)
            {
                _widthPercents[i] = EffectiveMinimumWidthPercent + extraPerSlot;
            }

            _layoutInitialized = true;
            RebuildGateGeometry();
        }

        [ContextMenu("Initialize Slots")]
        private void InitializeSlots()
        {
            if (_slotsInitialized)
            {
                return;
            }

            if (slots == null || slots.Length == 0 || slots[0] == null)
            {
                slots = GetComponentsInChildren<StatModifierGate>(true);
            }

            _slotCount = 0;
            for (int i = 0; i < slots.Length && i < _damageTotals.Length; i++)
            {
                if (slots[i] == null)
                {
                    continue;
                }

                slots[_slotCount] = slots[i];
                _slotCount++;
            }

            for (int i = _slotCount; i < slots.Length; i++)
            {
                slots[i] = null;
            }

            _slotsInitialized = true;
        }

        private int GetSlotIndex(StatModifierGate slot)
        {
            for (int i = 0; i < _slotCount; i++)
            {
                if (slots[i] == slot)
                {
                    return i;
                }
            }
            return -1;
        }

        private void RebuildGateGeometry()
        {
            float cursor = -totalWidth * 0.5f;
            for (int i = 0; i < _slotCount; i++)
            {
                StatModifierGate slot = slots[i];
                if (slot == null)
                {
                    continue;
                }

                float width = totalWidth * Mathf.Clamp(_widthPercents[i], 0f, 100f) * 0.01f;
                Vector3 localPosition = slot.transform.localPosition;
                localPosition.x = cursor + width * 0.5f;
                slot.transform.localPosition = localPosition;
                slot.ApplyRuntimeWidth(width, _referenceWidths[i]);
                slot.NotifyCollisionPositionChanged();
                cursor += width;
            }
        }
    }
}
