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

        [SerializeField] private List<VAT_RenderComponent> _registeredAnimators = new List<VAT_RenderComponent>();
        private readonly List<VAT_RenderComponent> _pendingRegisterAnimators = new List<VAT_RenderComponent>();
        private readonly List<VAT_RenderComponent> _pendingUnregisterAnimators = new List<VAT_RenderComponent>();

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

        public void RegisterAnimator(VAT_RenderComponent animator)
        {
            if (animator == null) return;

            if (!_pendingRegisterAnimators.Contains(animator) && !_registeredAnimators.Contains(animator))
            {
                _pendingRegisterAnimators.Add(animator);
            }

            if (_pendingUnregisterAnimators.Contains(animator))
            {
                _pendingUnregisterAnimators.Remove(animator);
            }
        }

        public void UnregisterAnimator(VAT_RenderComponent animator)
        {
            if (animator == null) return;

            if (!_pendingUnregisterAnimators.Contains(animator) && (_registeredAnimators.Contains(animator) || _pendingRegisterAnimators.Contains(animator)))
            {
                _pendingUnregisterAnimators.Add(animator);
            }

            if (_pendingRegisterAnimators.Contains(animator))
            {
                _pendingRegisterAnimators.Remove(animator);
            }
        }

        private void ProcessPendingRequests()
        {
            // Process pending unregistrations first
            int unregCount = _pendingUnregisterAnimators.Count;
            if (unregCount > 0)
            {
                for (int i = 0; i < unregCount; i++)
                {
                    _registeredAnimators.Remove(_pendingUnregisterAnimators[i]);
                }
                _pendingUnregisterAnimators.Clear();
            }

            // Process pending registrations
            int regCount = _pendingRegisterAnimators.Count;
            if (regCount > 0)
            {
                for (int i = 0; i < regCount; i++)
                {
                    VAT_RenderComponent anim = _pendingRegisterAnimators[i];
                    if (anim != null && !_registeredAnimators.Contains(anim))
                    {
                        _registeredAnimators.Add(anim);
                    }
                }
                _pendingRegisterAnimators.Clear();
            }
        }

        [Header("Frustum Culling Settings")]
        [SerializeField] private bool _enableCulling = true;
        [SerializeField] private float _cullInterval = 0.1f; // 10Hz check to save CPU cycles

        private Camera _mainCamera;
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
