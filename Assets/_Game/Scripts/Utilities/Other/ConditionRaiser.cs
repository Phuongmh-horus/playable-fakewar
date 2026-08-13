using UnityEngine;
using System;

public class ConditionRaiser : MonoBehaviour
{
    public virtual float WaitTime => 0f;

    public event Action<ConditionRaiser> Raised;

    [ContextMenu("Raise Condition")]
    public virtual void Raise()
    {
        Raised?.Invoke(this);
    }
}
