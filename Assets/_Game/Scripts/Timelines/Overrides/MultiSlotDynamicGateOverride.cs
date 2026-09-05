using System;
using GamePlay.Items;

[Serializable]
public class MultiSlotGateSlotOverride
{
    public bool overrideSlot;
    public StatModifierOperation operation = StatModifierOperation.Add;
    public int value = 1;
    public float multiplier = 1f;
    public int armor;
    public int maxHealth = 10;
}

[Serializable]
public class MultiSlotDynamicGateOverride : ItemUnitPropertyOverride
{
    public bool overrideWidthLayout;
    public float defaultWidthGrowPercent = 4f;
    public float defaultMinimumWidthPercent = 10f;
    public float totalWidth;
    public MultiSlotGateSlotOverride[] slots = new MultiSlotGateSlotOverride[3]
    {
        new MultiSlotGateSlotOverride(),
        new MultiSlotGateSlotOverride(),
        new MultiSlotGateSlotOverride()
    };

    public override void ApplyOverrides(ItemUnit itemUnit)
    {
        var target = itemUnit as MultiSlotDynamicGate;
        if (target == null)
        {
            return;
        }

        float configuredWidthGrowPercent = overrideWidthLayout ? defaultWidthGrowPercent : target.DefaultWidthGrowPercent;
        float configuredMinimumWidthPercent = overrideWidthLayout ? defaultMinimumWidthPercent : target.DefaultMinimumWidthPercent;
        float configuredTotalWidth = overrideWidthLayout ? totalWidth : target.TotalWidth;
        target.ApplyContentOverride(configuredWidthGrowPercent, configuredMinimumWidthPercent, configuredTotalWidth, slots);
    }
}