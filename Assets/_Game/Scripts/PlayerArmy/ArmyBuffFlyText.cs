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

        public void Show(StatType statType)
        {
            flyTextEffect?.ShowCustomText(GetText(statType), textColor, fontSizeMultiplier);
        }

        private void ResolveDependencies()
        {
            if (flyTextEffect == null)
            {
                flyTextEffect = GetComponent<HitTextFlyEffect>();
            }
        }

        private static string GetText(StatType statType)
        {
            switch (statType)
            {
                case StatType.FireRate:
                    return "+ firerate";
                case StatType.Damage:
                    return "+ damage";
                case StatType.Character:
                    return "+ unit";
                case StatType.CharacterLevel:
                    return "upgrade";
                default:
                    return "+ damage";
            }
        }
    }
}