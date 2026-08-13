using System.Collections.Generic;
using UnityEngine;

public class MeshRendererCullingObject : CullingObject
{
    [SerializeField] private List<Renderer> targetRenderers = new List<Renderer>();
    [SerializeField] private List<Animator> targetAnimators = new List<Animator>();
    private readonly Dictionary<Animator, AnimatorCullingMode> _animatorCullingModes = new Dictionary<Animator, AnimatorCullingMode>(8);

    [ContextMenu("Collects")]
    private void Start()
    {
        EnsureTargetsCached();
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    protected override void ApplyCulling(bool culled)
    {
        EnsureTargetsCached();
        if (targetRenderers == null) return;

        for (int i = 0; i < targetRenderers.Count; i++)
        {
            if (targetRenderers[i] != null)
            {
                targetRenderers[i].enabled = !culled;
            }
        }

        if (targetAnimators == null) return;

        for (int i = 0; i < targetAnimators.Count; i++)
        {
            var animator = targetAnimators[i];
            if (animator == null) continue;

            if (!_animatorCullingModes.TryGetValue(animator, out var defaultMode))
            {
                defaultMode = animator.cullingMode;
                _animatorCullingModes[animator] = defaultMode;
            }

            animator.cullingMode = culled ? AnimatorCullingMode.CullCompletely : defaultMode;
        }
    }

    private void CacheAnimatorModes()
    {
        _animatorCullingModes.Clear();
        if (targetAnimators == null) return;

        for (int i = 0; i < targetAnimators.Count; i++)
        {
            var animator = targetAnimators[i];
            if (animator == null) continue;
            _animatorCullingModes[animator] = animator.cullingMode;
        }
    }

    private void EnsureTargetsCached()
    {
        if (targetRenderers == null || targetRenderers.Count == 0)
        {
            targetRenderers = new List<Renderer>(GetComponentsInChildren<Renderer>(true));
        }

        if (targetAnimators == null || targetAnimators.Count == 0)
        {
            targetAnimators = new List<Animator>(GetComponentsInChildren<Animator>(true));
        }

        if (_animatorCullingModes.Count == 0)
        {
            CacheAnimatorModes();
        }
    }
}
