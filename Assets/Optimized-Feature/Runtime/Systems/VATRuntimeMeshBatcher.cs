using System;
using System.Collections.Generic;
using UnityEngine;

namespace OptimizedFeature.Scripts
{
    /// <summary>
    /// Luna-compatible runtime batching boundary.
    /// Luna's Unity API does not expose Graphics.DrawMeshInstanced, and the
    /// former combined-mesh fallback caused per-frame channel uploads and
    /// renderer lifecycle allocations. Keep source renderers active instead.
    /// </summary>
    internal sealed class VATRuntimeMeshBatcher : IDisposable
    {
        private readonly Dictionary<MeshRenderer, Source> _hiddenSources =
            new Dictionary<MeshRenderer, Source>(128);

        internal struct Source
        {
            internal MeshFilter MeshFilter;
            internal MeshRenderer Renderer;
            internal Mesh Mesh;
            internal Material Material;
            internal VAT_RenderComponent Owner;
            internal VATWeaponRenderComponent Weapon;
            internal Vector3 BoundsMin;
            internal Vector3 BoundsMax;
            internal float FrameLower;
            internal float FrameUpper;
            internal float BlendWeight;
        }

        internal void UpdateBatches(IList<VAT_RenderComponent> animators)
        {
            RestoreOriginalRenderers();
        }

        internal void Clear()
        {
            RestoreOriginalRenderers();
        }

        public void Dispose()
        {
            Clear();
        }

        private void RestoreOriginalRenderers()
        {
            foreach (KeyValuePair<MeshRenderer, Source> pair in _hiddenSources)
            {
                SetSourceRuntimeBatchHidden(pair.Value, false);
            }

            _hiddenSources.Clear();
        }

        private static void SetSourceRuntimeBatchHidden(Source source, bool hidden)
        {
            if (source.Weapon != null) source.Weapon.SetRuntimeBatchHidden(hidden);
            else if (source.Owner != null) source.Owner.SetRuntimeBatchHidden(hidden);
        }
    }
}
