using UnityEngine;

public class AnimatorParameterRaiser : ConditionRaiser
{
    [SerializeField] private Animator animator;
    [SerializeField] private string parameterName;
    [SerializeField] private float waitTime = 0f;
    [SerializeField] private float waitDropTime = 0f;

    public override float WaitTime => waitTime;
    public float WaitDropTime => waitDropTime;

    public override void Raise()
    {
        if (animator != null && !string.IsNullOrEmpty(parameterName))
        {
            animator.SetTrigger(parameterName);
        }

        base.Raise();
    }
}
