using System.Collections.Generic;
using GamePlay.Items;
using OptimizedFeature.Scripts;
using UnityEngine;

namespace GamePlay.Rendering
{
    [DisallowMultipleComponent]
    public sealed class PlayableRenderVisibilitySystem : MonoBehaviour
    {
        private const float RefreshInterval = 0.3f;
        private const float AheadDistance = 210f;
        private const float BehindDistance = 20f;

        private struct Entry
        {
            public Renderer Renderer;
            public VAT_RenderComponent VatRenderer;
            public Transform Anchor;
            public bool WasEnabled;
            public bool IsVisible;
        }

        private readonly List<Entry> _entries = new List<Entry>(512);
        private Transform _focus;
        private float _nextRefreshTime;

        public void Configure(Camera camera, Transform focus, IList<ItemUnit> items)
        {
            _focus = focus;
            _entries.Clear();
            _nextRefreshTime = 0f;

            if (items == null)
            {
                return;
            }

            for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
            {
                ItemUnit item = items[itemIndex];
                if (item == null)
                {
                    continue;
                }

                Renderer[] renderers = item.GetComponentsInChildren<Renderer>(true);
                for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                {
                    Renderer renderer = renderers[rendererIndex];
                    if (renderer == null)
                    {
                        continue;
                    }

                    _entries.Add(new Entry
                    {
                        Renderer = renderer,
                        VatRenderer = renderer.GetComponent<VAT_RenderComponent>(),
                        Anchor = item.transform,
                        WasEnabled = renderer.enabled,
                        IsVisible = renderer.enabled
                    });
                }
            }

            RefreshVisibility();
        }

        private void Update()
        {
            if (!GameplayManager.IsGameStarted || Time.time < _nextRefreshTime)
            {
                return;
            }

            RefreshVisibility();
        }

        private void OnDisable()
        {
            SetAllVisible();
        }

        private void RefreshVisibility()
        {
            _nextRefreshTime = Time.time + RefreshInterval;
            if (_focus == null)
            {
                return;
            }

            Vector3 focusPosition = _focus.position;
            Vector3 forward = _focus.forward;

            for (int index = _entries.Count - 1; index >= 0; index--)
            {
                Entry entry = _entries[index];
                Renderer renderer = entry.Renderer;
                if (renderer == null || entry.Anchor == null)
                {
                    RemoveAtSwapBack(index);
                    continue;
                }

                float forwardDistance = Vector3.Dot(entry.Anchor.position - focusPosition, forward);
                bool shouldBeVisible = entry.WasEnabled &&
                                       forwardDistance >= -BehindDistance &&
                                       forwardDistance <= AheadDistance;

                if (entry.IsVisible != shouldBeVisible)
                {
                    if (entry.VatRenderer != null)
                    {
                        entry.VatRenderer.SetExternalVisibility(shouldBeVisible);
                    }
                    else
                    {
                        renderer.enabled = shouldBeVisible;
                    }
                    entry.IsVisible = shouldBeVisible;
                    _entries[index] = entry;
                }
            }
        }

        private void SetAllVisible()
        {
            for (int index = 0; index < _entries.Count; index++)
            {
                Entry entry = _entries[index];
                if (entry.Renderer != null)
                {
                    if (entry.VatRenderer != null)
                    {
                        entry.VatRenderer.SetExternalVisibility(true);
                    }
                    else
                    {
                        entry.Renderer.enabled = entry.WasEnabled;
                    }
                }
            }
        }

        private void RemoveAtSwapBack(int index)
        {
            int lastIndex = _entries.Count - 1;
            if (index != lastIndex)
            {
                _entries[index] = _entries[lastIndex];
            }

            _entries.RemoveAt(lastIndex);
        }
    }
}