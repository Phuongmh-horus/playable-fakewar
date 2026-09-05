using System;

namespace GamePlay.Items
{
    public enum StatModifierOperation
    {
        Add = 0,
        Multiply = 1
    }

    [Serializable] // Bắt buộc để hiện trong Inspector
    public class StatModifierGateData : StatModifierData
    {
        public StatModifierOperation Operation = StatModifierOperation.Add;

        [UnityEngine.Tooltip("Giá trị dùng khi Operation là Multiply. Nhỏ hơn 1 sẽ giảm số lượng character.")]
        public float Multiplier = 1f;
    }

}
