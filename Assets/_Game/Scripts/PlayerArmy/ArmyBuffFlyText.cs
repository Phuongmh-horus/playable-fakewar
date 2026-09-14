using GamePlay.Items;
using UnityEngine;

namespace PlayerArmy
{
    [DisallowMultipleComponent]
    public class ArmyBuffFlyText : MonoBehaviour
    {
        [SerializeField] private HitTextFlyEffect flyTextEffect;
        [SerializeField] private Color textColor = Color.yellow;
        [SerializeField, Min(0f)] private float fontSizeMultiplier = 1f;

        private void Awake()
        {
            ResolveDependencies();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            ResolveDependencies();
        }
#endif

        public void Show(StatModifierData statData)
        {
            string text = GetText(statData);
            if (!string.IsNullOrEmpty(text))
            {
                flyTextEffect?.ShowCustomText(text, textColor, fontSizeMultiplier);
            }
        }

        public void Show(StatType statType)
        {
            Show(new StatModifierData { Type = statType, ShowArmyBuffFlyText = true });
        }

        private void ResolveDependencies()
        {
            if (flyTextEffect == null)
            {
                flyTextEffect = GetComponent<HitTextFlyEffect>();
            }
        }

        private static string GetText(StatModifierData statData)
        {
            if (statData == null || !statData.ShowArmyBuffFlyText)
            {
                return string.Empty;
            }

            switch (statData.Type)
            {
                case StatType.FireRate:
                    return FormatPercent(statData, "FIRE", 10);
                case StatType.Damage:
                    return FormatPercent(statData, "ATK", 20);
                case StatType.CharacterLevel:
                    return "UPGRADE";
                case StatType.Character:
                    return string.Empty;
                default:
                    return string.Empty;
            }
        }

        private static string FormatPercent(StatModifierData statData, string defaultLabel, int defaultMultiplier)
        {
            int percent = statData.ArmyBuffFlyTextPercent > 0
                ? statData.ArmyBuffFlyTextPercent
                : Mathf.Max(0, statData.Value * defaultMultiplier);
            if (percent <= 0)
            {
                return string.Empty;
            }

            string label = string.IsNullOrWhiteSpace(statData.ArmyBuffFlyTextLabel)
                ? defaultLabel
                : statData.ArmyBuffFlyTextLabel.Trim();
            return $"{label} + {percent}%";
        }
    }
}
