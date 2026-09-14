using System;
using DG.Tweening;
using GamePlay.ComponentSystems;
using OptimizedFeature.Scripts;
using UnityEngine;

namespace GamePlay.AnimationSystems
{
    public class VATAnimationComponent : BaseComponent, IAnimator, IAnimationClipLengthProvider
    {
        [SerializeField] private VAT_RenderComponent vatRenderer;

        [Header("VAT State Names")]
        [SerializeField] private string idleStateName = "";
        [SerializeField] private string attackStateName = "";
        [SerializeField] private string deathStateName = "";
        [SerializeField] private string MoveStateName = "";
        [SerializeField] private string moveLeftStateName = "";
        [SerializeField] private string moveRightStateName = "";
        [SerializeField, Min(0f)] private float crossFadeDuration = 0.1f;

        private AnimationType _currentAnimation = AnimationType.None;
        private Action _pendingCompletion;
        private TweenCallback _cachedCompletionCallback;

        protected override void Awake()
        {
            base.Awake();
            ResolveVATRenderer();
            _cachedCompletionCallback = InvokePendingCompletion;
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            ResolveVATRenderer();
        }
#endif

        public override void Initialize()
        {
            base.Initialize();
            ResolveVATRenderer();
            PlayAnimation(AnimationType.Idle, 0f);
        }

        public override void Dispose()
        {
            DOTween.Kill(this);
            _pendingCompletion = null;
            _currentAnimation = AnimationType.None;
            base.Dispose();
        }

        public void PlayAnimation(AnimationType animationType, float waitForAction = 0.5f, Action onComplete = null, int layer = 0)
        {
            string stateName = ResolveStateName(animationType);
            if (vatRenderer != null && !string.IsNullOrEmpty(stateName))
            {
                int stateHash = VATClipInfo.GenerateHash(stateName);
                bool stateChanged = _currentAnimation != animationType || vatRenderer.CurrentStateHash != stateHash;
                if (stateChanged && animationType != AnimationType.Death && crossFadeDuration > 0f && vatRenderer.CurrentStateHash != 0)
                {
                    vatRenderer.CrossFade(stateHash, crossFadeDuration);
                }
                else if (stateChanged)
                {
                    vatRenderer.Play(stateHash);
                }

                if (stateChanged)
                {
                    _currentAnimation = animationType;
                }
            }

            if (onComplete == null)
            {
                return;
            }

            DOTween.Kill(this);
            _pendingCompletion = onComplete;
            if (waitForAction <= 0f)
            {
                InvokePendingCompletion();
                return;
            }

            DOVirtual.DelayedCall(waitForAction, _cachedCompletionCallback, false).SetId(this);
        }

        public float GetAnimationClipLength(AnimationType animationType)
        {
            string stateName = ResolveStateName(animationType);
            VATAssetDataSO assetData = vatRenderer != null ? vatRenderer.VatAssetData : null;
            VATClipInfo clip = assetData != null && !string.IsNullOrEmpty(stateName)
                ? assetData.GetClip(VATClipInfo.GenerateHash(stateName))
                : null;

            if (clip == null || clip.FrameRate <= 0f)
            {
                return 0f;
            }

            return (clip.EndFrame - clip.StartFrame + 1) / clip.FrameRate;
        }

        public void SetDeathDesaturation(float amount)
        {
            ResolveVATRenderer();
            vatRenderer?.SetDeathDesaturation(amount);
        }

        private string ResolveStateName(AnimationType animationType)
        {
            switch (animationType)
            {
                case AnimationType.Idle:
                    return idleStateName;
                case AnimationType.Move:
                    return MoveStateName;
                case AnimationType.Attack:
                    return attackStateName;
                case AnimationType.Death:
                    return deathStateName;
                case AnimationType.MoveLeft:
                    return moveLeftStateName;
                case AnimationType.MoveRight:
                    return moveRightStateName;
                default:
                    return null;
            }
        }

        private void ResolveVATRenderer()
        {
            if (vatRenderer == null)
            {
                vatRenderer = GetComponentInChildren<VAT_RenderComponent>(true);
            }
        }

        private void InvokePendingCompletion()
        {
            Action completion = _pendingCompletion;
            _pendingCompletion = null;
            completion?.Invoke();
        }
    }
}
