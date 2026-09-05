using System;
using GamePlay.Items;

[Serializable]
public class FireGateOverride : ItemUnitPropertyOverride
{
    public bool overrideValue;
    public int  Value = 2;
    public StatModifierOperation Operation = StatModifierOperation.Add;
    public float Multiplier = 1f;
    public int  Armor = 0;

    public float LeftOffset;
    public float RightOffset;

    public override void ApplyOverrides(ItemUnit itemUnit)
    {
        var target = itemUnit as StatModifierGate;
        if (target == null || target.Data == null) return;

        target.Data.Value  = Value;
        target.Data.Operation = Operation;
        target.Data.Multiplier = Multiplier;
        target.Data.Armor  = Armor;
        target.LeftOffset  = LeftOffset;
        target.RightOffset = RightOffset;
    }
}
