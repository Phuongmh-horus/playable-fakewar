using System;
using System.Collections.Generic;
using GamePlay.Crushers;
using UnityEngine;

namespace GamePlay.Items
{
    [Serializable]
    public class SoldierBallData : StatModifierData
    {
        public enum EChangeType
        {
            Increase = 0,
            Upgrade = 1,
        }

        public EChangeType ChangeType;
        public int Level;

        public override void AdjustValue(int amount)
        {
            if (Armor > 0)
            {
                if (amount != 0)
                {
                    Armor -= 1;
                }
            }
        }

    }
}
