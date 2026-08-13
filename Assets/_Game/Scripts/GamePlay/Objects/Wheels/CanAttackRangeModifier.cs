using UnityEngine;

public class CanAttackRangeModifier : MonoBehaviour
{
    [SerializeField] private Vector2 bonusConfig;

    public void ApplyBonus()
    {
        int bonusPoint = Mathf.Max(0, Mathf.RoundToInt(bonusConfig.x + bonusConfig.y));
        if (bonusPoint <= 0)
        {
            return;
        }

        GameplayManager.Instance?.ActiveArmy?.ApplyFireRangeModifier(bonusPoint);
    }
}
