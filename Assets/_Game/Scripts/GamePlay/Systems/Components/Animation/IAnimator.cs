using System;
using GamePlay.ComponentSystems;

namespace GamePlay.AnimationSystems
{
    public interface IAnimator : IComponent
    {
        void PlayAnimation(AnimationType animationType, float waitForAction = 0.5f, Action onComplete = null, int layer = 0);
    }

    public interface IAnimationClipLengthProvider
    {
        float GetAnimationClipLength(AnimationType animationType);
    }

    public enum AnimationType : byte
    {
        None,
        Idle,
        Move,
        Strafe,
        Rotate,
        Attack,
        Jump,
        Death,
        ConveyorJump,
        Break,
        MoveLeft,
        MoveRight,
    }
}
