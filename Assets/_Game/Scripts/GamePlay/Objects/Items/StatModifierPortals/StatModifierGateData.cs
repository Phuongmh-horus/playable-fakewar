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
    }

}
