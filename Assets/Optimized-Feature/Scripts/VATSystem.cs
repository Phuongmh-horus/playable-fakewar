using System.Collections.Generic;
using UnityEngine;

namespace OptimizedFeature.Scripts
{
    /// <summary>
    /// Core System driving execution logic for all active VAT_RenderComponent instances.
    /// Completely decoupled from gameplay logic and operates without GC allocations.
    /// Manages pending lists for frame-safe registration and unregistration.
    /// </summary>
    public class VATSystem : MonoBehaviour
    {
        public static VATSystem Instance { get; private set; }

        // Runtime state must stay out of serialized scene data. VAT_RenderComponent
        // only communicates through the static queue APIs below, so its lifecycle
        // does not depend on script execution order or a scene-wide lookup.
        private readonly List<VAT_RenderComponent> _registeredAnimators = new List<VAT_RenderComponent>();
        private static readonly List<VAT_RenderComponent> PendingRegisterAnimators = new List<VAT_RenderComponent>();
        private static readonly List<VAT_RenderComponent> PendingUnregisterAnimators = new List<VAT_RenderComponent>();

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else if (Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            ProcessPendingRequests();
        }

        /// <summary>
        /// Queues a VAT renderer for the active system. Safe to call from OnEnable
        /// even when VATSystem.Awake has not run yet.
        /// </summary>
        public static void RegisterAnimator(VAT_RenderComponent animator)
        {
            if (animator == null) return;

            if (PendingUnregisterAnimators.Contains(animator))
            {
                PendingUnregisterAnimators.Remove(animator);
            }

            if (Instance != null && Instance._registeredAnimators.Contains(animator))
            {
                return;
            }

            if (!PendingRegisterAnimators.Contains(animator))
            {
                PendingRegisterAnimators.Add(animator);
            }
        }

        /// <summary>
        /// Cancels a pending registration or queues removal from the active system.
        /// </summary>
        public static void UnregisterAnimator(VAT_RenderComponent animator)
        {
            if (animator == null) return;

            PendingRegisterAnimators.Remove(animator);

            if (Instance != null && Instance._registeredAnimators.Contains(animator) &&
                !PendingUnregisterAnimators.Contains(animator))
            {
                PendingUnregisterAnimators.Add(animator);
            }
        }

        private void OnDestroy()
        {
            if (Instance != this) return;

            // Preserve live components if a replacement manager is created after
            // a scene transition. Invalid Unity references are filtered and the
            // static queue is cleared by that manager's first processing pass.
            int count = _registeredAnimators.Count;
            for (int i = 0; i < count; i++)
            {
                VAT_RenderComponent animator = _registeredAnimators[i];
                if (animator != null && !PendingRegisterAnimators.Contains(animator))
                {
                    PendingRegisterAnimators.Add(animator);
                }
            }

            _registeredAnimators.Clear();
            Instance = null;
        }

        private void ProcessPendingRequests()
        {
            // Process pending unregistrations first
            int unregCount = PendingUnregisterAnimators.Count;
            if (unregCount > 0)
            {
                for (int i = 0; i < unregCount; i++)
                {
                    _registeredAnimators.Remove(PendingUnregisterAnimators[i]);
                }
                PendingUnregisterAnimators.Clear();
            }

            // Process and immediately release registrations. The queues are only
            // an execution-order bridge; keeping entries after completion would
            // retain stale component references across scene changes.
            int regCount = PendingRegisterAnimators.Count;
            if (regCount > 0)
            {
                for (int i = 0; i < regCount; i++)
                {
                    VAT_RenderComponent anim = PendingRegisterAnimators[i];
                    if (anim != null && anim.enabled && anim.gameObject.activeInHierarchy &&
                        !_registeredAnimators.Contains(anim))
                    {
                        _registeredAnimators.Add(anim);
                    }
                }
                PendingRegisterAnimators.Clear();
            }
        }

        [Header("Frustum Culling Settings")]
        [SerializeField] private bool _enableCulling = true;
        [SerializeField] private float _cullInterval = 0.1f; // 10Hz check to save CPU cycles
        [Tooltip("Camera used for VAT frustum culling. If empty or destroyed, VATSystem falls back to Camera.main.")]
        [SerializeField] private Camera _mainCamera;
        private float _cullTimer = 0f;

        private void Update()
        {
            ProcessPendingRequests();

            float deltaTime = Time.deltaTime;
            int count = _registeredAnimators.Count;

            // Trigger culling check at the specified interval
            bool runCullCheck = false;
            if (_enableCulling)
            {
                _cullTimer += deltaTime;
                if (_cullTimer >= _cullInterval)
                {
                    _cullTimer = 0f;
                    runCullCheck = true;
                    // A manually assigned camera has priority. Camera.main is
                    // only a fallback when no valid explicit reference exists.
                    if (_mainCamera == null)
                    {
                        _mainCamera = Camera.main;
                    }
                }
            }

            // Direct index loop avoiding enumerator GC allocations
            for (int i = 0; i < count; i++)
            {
                VAT_RenderComponent anim = _registeredAnimators[i];
                if (anim != null && anim.enabled && anim.gameObject.activeInHierarchy)
                {
                    if (_enableCulling)
                    {
                        if (runCullCheck)
                        {
                            bool isVisible = true;
                            if (_mainCamera != null)
                            {
                                Bounds worldBounds = anim.GetWorldBounds();
                                Vector3 center = worldBounds.center;
                                float radius = worldBounds.extents.magnitude;

                                // Project world center to camera viewport coordinates
                                Vector3 viewportCenter = _mainCamera.WorldToViewportPoint(center);
                                if (viewportCenter.z >= 0f)
                                {
                                    // Scale padding margins based on distance to camera (nearer needs larger margin)
                                    float marginX = radius / Mathf.Max(0.1f, viewportCenter.z);
                                    float marginY = radius / Mathf.Max(0.1f, viewportCenter.z);

                                    isVisible = (viewportCenter.x >= -marginX && viewportCenter.x <= 1.0f + marginX &&
                                                 viewportCenter.y >= -marginY && viewportCenter.y <= 1.0f + marginY);
                                }
                                else
                                {
                                    // Completely behind camera
                                    isVisible = false;
                                }
                            }
                            anim.SetVisibility(isVisible);
                        }
                        anim.ManualUpdate(deltaTime, anim.IsVisible);
                    }
                    else
                    {
                        anim.SetVisibility(true);
                        anim.ManualUpdate(deltaTime, true);
                    }
                }
            }
        }
    }
}
