using System;
using GamePlay.Items;

[Serializable]
public class SoldierBallOverride : ItemUnitPropertyOverride
{
    public bool overrideValue;
    public SoldierBallData.EChangeType ChangeType;
    public int Value;
    public int Level;
    public float LeftOffset;
    public float RightOffset;

    public override void ApplyOverrides(ItemUnit itemUnit)
    {
        SoldierBall target = itemUnit as SoldierBall;
        if (target == null || target.Data == null)
        {
            return;
        }

        target.Data.ChangeType = ChangeType;
        target.Data.Value = Value;
        target.Data.Level = Level;
        target.LeftOffset = LeftOffset;
        target.RightOffset = RightOffset;
    }
}
