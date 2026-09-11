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
            public bool WasEnabled;
        }

        private struct Group
        {
            public Transform Anchor;
            public int FirstEntry;
            public int EntryCount;
            public bool IsVisible;
        }

        private readonly List<Entry> _entries = new List<Entry>(512);
        private readonly List<Group> _groups = new List<Group>(128);
        private readonly List<Renderer> _rendererBuffer = new List<Renderer>(32);
        private Transform _focus;
        private float _nextRefreshTime;

        public void Configure(Camera camera, Transform focus, IList<ItemUnit> items)
        {
            _focus = focus;
            _entries.Clear();
            _groups.Clear();
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

                _rendererBuffer.Clear();
                item.GetComponentsInChildren(true, _rendererBuffer);
                int firstEntry = _entries.Count;
                for (int rendererIndex = 0; rendererIndex < _rendererBuffer.Count; rendererIndex++)
                {
                    Renderer renderer = _rendererBuffer[rendererIndex];
                    if (renderer == null)
                    {
                        continue;
                    }

                    VAT_RenderComponent vatRenderer = renderer.GetComponent<VAT_RenderComponent>();
                    if (vatRenderer == null &&
                        renderer.GetComponent<VATWeaponRenderComponent>() != null)
                    {
                        // The parent VAT renderer owns weapon visibility and also
                        // accounts for an unequipped/null weapon asset. Registering
                        // this renderer again would duplicate native state writes
                        // and could incorrectly re-enable an empty weapon renderer.
                        continue;
                    }

                    if (vatRenderer != null)
                    {
                        // VATSystem may have internally culled a distant actor before this system is configured. 
                        // That runtime state is not the prefab's authored enabled state and must not permanently lock the actor off.
                        vatRenderer.SetExternalVisibility(true);
                    }

                    _entries.Add(new Entry
                    {
                        Renderer = renderer,
                        VatRenderer = vatRenderer,
                        WasEnabled = vatRenderer != null || renderer.enabled
                    });
                }

                int entryCount = _entries.Count - firstEntry;
                if (entryCount > 0)
                {
                    _groups.Add(new Group
                    {
                        Anchor = item.transform,
                        FirstEntry = firstEntry,
                        EntryCount = entryCount,
                        IsVisible = true
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

            for (int groupIndex = _groups.Count - 1; groupIndex >= 0; groupIndex--)
            {
                Group group = _groups[groupIndex];
                if (group.Anchor == null)
                {
                    RemoveGroupAtSwapBack(groupIndex);
                    continue;
                }

                float forwardDistance = Vector3.Dot(group.Anchor.position - focusPosition, forward);
                bool shouldBeVisible = forwardDistance >= -BehindDistance &&
                                       forwardDistance <= AheadDistance;

                if (group.IsVisible == shouldBeVisible)
                {
                    continue;
                }

                SetGroupVisible(group, shouldBeVisible);
                group.IsVisible = shouldBeVisible;
                _groups[groupIndex] = group;
            }
        }

        private void SetGroupVisible(Group group, bool visible)
        {
            int lastEntry = Mathf.Min(_entries.Count, group.FirstEntry + group.EntryCount);
            for (int index = group.FirstEntry; index < lastEntry; index++)
            {
                Entry entry = _entries[index];
                Renderer renderer = entry.Renderer;
                if (renderer == null)
                {
                    continue;
                }

                bool rendererVisible = visible && entry.WasEnabled;
                if (entry.VatRenderer != null)
                {
                    entry.VatRenderer.SetExternalVisibility(rendererVisible);
                }
                else
                {
                    if (renderer.enabled != rendererVisible)
                    {
                        renderer.enabled = rendererVisible;
                    }
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
                        if (entry.Renderer.enabled != entry.WasEnabled)
                        {
                            entry.Renderer.enabled = entry.WasEnabled;
                        }
                    }
                }
            }

            for (int index = 0; index < _groups.Count; index++)
            {
                Group group = _groups[index];
                group.IsVisible = true;
                _groups[index] = group;
            }
        }

        private void RemoveGroupAtSwapBack(int index)
        {
            int lastIndex = _groups.Count - 1;
            if (index != lastIndex)
            {
                _groups[index] = _groups[lastIndex];
            }

            _groups.RemoveAt(lastIndex);
        }
    }
}
