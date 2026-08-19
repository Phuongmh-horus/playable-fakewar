using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace OptimizedFeature.Scripts
{
    /// <summary>
    /// Combines compatible VAT renderers into one runtime mesh. Unity's regular
    /// dynamic batching is unsafe for this shader because VAT reconstructs the
    /// position from a texture instead of using the incoming mesh position.
    ///
    /// Each combined vertex carries its source transform and animation frame in
    /// UV channels 2-5. The batch GameObject stays at identity, so the shader
    /// can render instances with different transforms and different frames in a
    /// single renderer.
    /// </summary>
    internal sealed class VATRuntimeMeshBatcher : IDisposable
    {
        private static readonly int VatBatchModeId = Shader.PropertyToID("_VATBatchMode");
        private const string VatShaderName = "OptimizedFeature/VAT_Unlit_Luna";
        private const string VatNoOutlineShaderName = "OptimizedFeature/VAT_Unlit_Luna_NoOutline";

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

        private struct HiddenRenderer
        {
            internal MeshRenderer Renderer;
            internal VAT_RenderComponent Owner;
            internal VATWeaponRenderComponent Weapon;
        }

        private struct BatchKey : IEquatable<BatchKey>
        {
            private readonly int _meshId;
            private readonly int _materialId;
            private readonly int _layer;
            private readonly uint _renderingLayerMask;
            private readonly ShadowCastingMode _shadowCastingMode;
            private readonly bool _receiveShadows;
            private readonly LightProbeUsage _lightProbeUsage;
            private readonly ReflectionProbeUsage _reflectionProbeUsage;
            private readonly MotionVectorGenerationMode _motionVectorGenerationMode;
            private readonly int _rendererPriority;
            private readonly int _sortingLayerId;
            private readonly int _sortingOrder;

            internal BatchKey(Source source)
            {
                _meshId = source.Mesh.GetInstanceID();
                _materialId = source.Material.GetInstanceID();
                _layer = source.Renderer.gameObject.layer;
                _renderingLayerMask = source.Renderer.renderingLayerMask;
                _shadowCastingMode = source.Renderer.shadowCastingMode;
                _receiveShadows = source.Renderer.receiveShadows;
                _lightProbeUsage = source.Renderer.lightProbeUsage;
                _reflectionProbeUsage = source.Renderer.reflectionProbeUsage;
                _motionVectorGenerationMode = source.Renderer.motionVectorGenerationMode;
                _rendererPriority = source.Renderer.rendererPriority;
                _sortingLayerId = source.Renderer.sortingLayerID;
                _sortingOrder = source.Renderer.sortingOrder;
            }

            public bool Equals(BatchKey other)
            {
                return _meshId == other._meshId &&
                       _materialId == other._materialId &&
                       _layer == other._layer &&
                       _renderingLayerMask == other._renderingLayerMask &&
                       _shadowCastingMode == other._shadowCastingMode &&
                       _receiveShadows == other._receiveShadows &&
                       _lightProbeUsage == other._lightProbeUsage &&
                       _reflectionProbeUsage == other._reflectionProbeUsage &&
                       _motionVectorGenerationMode == other._motionVectorGenerationMode &&
                       _rendererPriority == other._rendererPriority &&
                       _sortingLayerId == other._sortingLayerId &&
                       _sortingOrder == other._sortingOrder;
            }

            public override bool Equals(object obj)
            {
                return obj is BatchKey && Equals((BatchKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _meshId;
                    hash = hash * 31 + _materialId;
                    hash = hash * 31 + _layer;
                    hash = hash * 31 + _renderingLayerMask.GetHashCode();
                    hash = hash * 31 + (int)_shadowCastingMode;
                    hash = hash * 31 + (_receiveShadows ? 1 : 0);
                    hash = hash * 31 + (int)_lightProbeUsage;
                    hash = hash * 31 + (int)_reflectionProbeUsage;
                    hash = hash * 31 + (int)_motionVectorGenerationMode;
                    hash = hash * 31 + _rendererPriority;
                    hash = hash * 31 + _sortingLayerId;
                    hash = hash * 31 + _sortingOrder;
                    return hash;
                }
            }
        }

        private sealed class BatchGroup
        {
            internal BatchKey Key;
            internal readonly List<Source> Sources = new List<Source>();
            internal GameObject GameObject;
            internal MeshFilter MeshFilter;
            internal MeshRenderer Renderer;
            internal Mesh Mesh;
            internal int BuiltSourceCount;
            internal Vector3[] BaseNormals;
            internal readonly List<Vector3> Normals = new List<Vector3>();
            internal readonly List<Vector4> Transform0 = new List<Vector4>();
            internal readonly List<Vector4> Transform1 = new List<Vector4>();
            internal readonly List<Vector4> Transform2 = new List<Vector4>();
            internal readonly List<Vector4> Frames = new List<Vector4>();
        }

        private readonly Dictionary<BatchKey, BatchGroup> _groups =
            new Dictionary<BatchKey, BatchGroup>();
        private readonly Dictionary<int, Material> _batchMaterials =
            new Dictionary<int, Material>();
        private readonly List<Source> _sources = new List<Source>(128);
        private readonly List<HiddenRenderer> _hiddenRenderers = new List<HiddenRenderer>(128);
        private readonly HashSet<BatchKey> _activeKeys = new HashSet<BatchKey>();
        private readonly List<BatchKey> _staleKeys = new List<BatchKey>();

        internal void RestoreOriginalRenderers()
        {
            for (int i = 0; i < _hiddenRenderers.Count; i++)
            {
                HiddenRenderer hidden = _hiddenRenderers[i];
                if (hidden.Renderer == null) continue;

                bool visible = hidden.Weapon != null
                    ? hidden.Weapon.IsVisible
                    : hidden.Owner != null && hidden.Owner.IsVisible;
                hidden.Renderer.enabled = visible && hidden.Renderer.gameObject.activeInHierarchy;
            }

            _hiddenRenderers.Clear();
        }

        internal void UpdateBatches(IList<VAT_RenderComponent> animators)
        {
            if (animators == null) return;

            _sources.Clear();
            for (int i = 0; i < animators.Count; i++)
            {
                VAT_RenderComponent animator = animators[i];
                if (animator != null && animator.enabled && animator.gameObject.activeInHierarchy)
                {
                    animator.CollectRuntimeBatchSources(_sources);
                }
            }

            foreach (BatchGroup group in _groups.Values)
            {
                group.Sources.Clear();
            }
            _activeKeys.Clear();

            for (int i = 0; i < _sources.Count; i++)
            {
                Source source = _sources[i];
                if (!CanBatch(source)) continue;

                BatchKey key = new BatchKey(source);
                BatchGroup group;
                if (!_groups.TryGetValue(key, out group))
                {
                    group = new BatchGroup { Key = key };
                    _groups.Add(key, group);
                }

                group.Sources.Add(source);
                _activeKeys.Add(key);
            }

            foreach (KeyValuePair<BatchKey, BatchGroup> pair in _groups)
            {
                BatchGroup group = pair.Value;
                if (group.Sources.Count < 2)
                {
                    if (group.Renderer != null) group.Renderer.enabled = false;
                    continue;
                }

                EnsureGroup(group);
                if (group.Mesh == null || group.BaseNormals == null ||
                    group.BuiltSourceCount != group.Sources.Count)
                {
                    BuildCombinedMesh(group);
                }

                UpdateGroupData(group);
                group.Renderer.enabled = true;

                for (int i = 0; i < group.Sources.Count; i++)
                {
                    Source source = group.Sources[i];
                    if (source.Renderer == null) continue;

                    source.Renderer.enabled = false;
                    _hiddenRenderers.Add(new HiddenRenderer
                    {
                        Renderer = source.Renderer,
                        Owner = source.Owner,
                        Weapon = source.Weapon
                    });
                }
            }

            _staleKeys.Clear();
            foreach (KeyValuePair<BatchKey, BatchGroup> pair in _groups)
            {
                if (!_activeKeys.Contains(pair.Key))
                {
                    _staleKeys.Add(pair.Key);
                }
            }

            for (int i = 0; i < _staleKeys.Count; i++)
            {
                BatchKey key = _staleKeys[i];
                BatchGroup group = _groups[key];
                DestroyGroup(group);
                _groups.Remove(key);
            }
        }

        internal void Clear()
        {
            RestoreOriginalRenderers();

            foreach (BatchGroup group in _groups.Values)
            {
                DestroyGroup(group);
            }
            _groups.Clear();

            foreach (Material material in _batchMaterials.Values)
            {
                DestroyObject(material);
            }
            _batchMaterials.Clear();
            _sources.Clear();
            _activeKeys.Clear();
            _staleKeys.Clear();
        }

        public void Dispose()
        {
            Clear();
        }

        private static bool CanBatch(Source source)
        {
            if (source.MeshFilter == null || source.Renderer == null ||
                source.Mesh == null || source.Material == null)
            {
                return false;
            }

            if (!source.Renderer.enabled || !source.Renderer.gameObject.activeInHierarchy ||
                source.Mesh.vertexCount == 0 || source.Mesh.subMeshCount != 1 ||
                source.Mesh.GetTopology(0) != MeshTopology.Triangles)
            {
                return false;
            }

            Shader shader = source.Material.shader;
            return shader != null &&
                   (shader.name == VatShaderName || shader.name == VatNoOutlineShaderName) &&
                   source.Material.HasProperty("_VATTex");
        }

        private void EnsureGroup(BatchGroup group)
        {
            if (group.GameObject != null) return;

            Source source = group.Sources[0];
            GameObject batchObject = new GameObject(
                "VAT_RuntimeBatch_" + source.Mesh.name + "_" + source.Material.name);
            batchObject.hideFlags = HideFlags.HideAndDontSave;
            batchObject.layer = source.Renderer.gameObject.layer;
            batchObject.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            batchObject.transform.localScale = Vector3.one;

            group.GameObject = batchObject;
            group.MeshFilter = batchObject.AddComponent<MeshFilter>();
            group.Renderer = batchObject.AddComponent<MeshRenderer>();
            group.Renderer.sharedMaterial = GetBatchMaterial(source.Material);
            CopyRendererSettings(source.Renderer, group.Renderer);
            group.Renderer.enabled = false;
        }

        private Material GetBatchMaterial(Material sourceMaterial)
        {
            int materialId = sourceMaterial.GetInstanceID();
            Material batchMaterial;
            if (_batchMaterials.TryGetValue(materialId, out batchMaterial) && batchMaterial != null)
            {
                return batchMaterial;
            }

            batchMaterial = new Material(sourceMaterial)
            {
                name = sourceMaterial.name + " (VAT Runtime Batch)",
                hideFlags = HideFlags.HideAndDontSave,
                enableInstancing = false
            };
            batchMaterial.SetFloat(VatBatchModeId, 1f);
            _batchMaterials[materialId] = batchMaterial;
            return batchMaterial;
        }

        private static void CopyRendererSettings(Renderer source, Renderer destination)
        {
            destination.shadowCastingMode = source.shadowCastingMode;
            destination.receiveShadows = source.receiveShadows;
            destination.lightProbeUsage = source.lightProbeUsage;
            destination.reflectionProbeUsage = source.reflectionProbeUsage;
            destination.renderingLayerMask = source.renderingLayerMask;
            destination.motionVectorGenerationMode = source.motionVectorGenerationMode;
            destination.rendererPriority = source.rendererPriority;
            destination.sortingLayerID = source.sortingLayerID;
            destination.sortingOrder = source.sortingOrder;
            destination.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
        }

        private static void BuildCombinedMesh(BatchGroup group)
        {
            Source first = group.Sources[0];
            Mesh sourceMesh = first.Mesh;
            int sourceVertexCount = sourceMesh.vertexCount;
            int sourceCount = group.Sources.Count;
            int totalVertexCount = sourceVertexCount * sourceCount;

            Vector3[] sourceVertices = sourceMesh.vertices;
            Vector3[] sourceNormals = sourceMesh.normals;
            Vector2[] sourceUv = sourceMesh.uv;
            Vector2[] sourceVatUv = sourceMesh.uv2;
            int[] sourceIndices = sourceMesh.GetIndices(0);

            List<Vector3> vertices = new List<Vector3>(totalVertexCount);
            List<Vector3> normals = new List<Vector3>(totalVertexCount);
            List<Vector2> uv = new List<Vector2>(totalVertexCount);
            List<Vector2> vatUv = new List<Vector2>(totalVertexCount);
            List<Vector4> transform0 = new List<Vector4>(totalVertexCount);
            List<Vector4> transform1 = new List<Vector4>(totalVertexCount);
            List<Vector4> transform2 = new List<Vector4>(totalVertexCount);
            List<Vector4> frames = new List<Vector4>(totalVertexCount);
            List<int> indices = new List<int>(sourceIndices.Length * sourceCount);

            group.Normals.Clear();
            group.Transform0.Clear();
            group.Transform1.Clear();
            group.Transform2.Clear();
            group.Frames.Clear();
            group.BaseNormals = new Vector3[sourceVertexCount];

            for (int vertexIndex = 0; vertexIndex < sourceVertexCount; vertexIndex++)
            {
                Vector3 normal = sourceNormals != null && sourceNormals.Length == sourceVertexCount
                    ? sourceNormals[vertexIndex]
                    : Vector3.up;
                group.BaseNormals[vertexIndex] = normal;
            }

            for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
            {
                int vertexOffset = sourceIndex * sourceVertexCount;
                for (int vertexIndex = 0; vertexIndex < sourceVertexCount; vertexIndex++)
                {
                    vertices.Add(sourceVertices[vertexIndex]);
                    normals.Add(group.BaseNormals[vertexIndex]);
                    group.Normals.Add(group.BaseNormals[vertexIndex]);
                    uv.Add(sourceUv != null && sourceUv.Length == sourceVertexCount
                        ? sourceUv[vertexIndex]
                        : Vector2.zero);
                    vatUv.Add(sourceVatUv != null && sourceVatUv.Length == sourceVertexCount
                        ? sourceVatUv[vertexIndex]
                        : Vector2.zero);
                    transform0.Add(Vector4.zero);
                    transform1.Add(Vector4.zero);
                    transform2.Add(Vector4.zero);
                    frames.Add(Vector4.zero);
                    group.Transform0.Add(Vector4.zero);
                    group.Transform1.Add(Vector4.zero);
                    group.Transform2.Add(Vector4.zero);
                    group.Frames.Add(Vector4.zero);
                }

                for (int index = 0; index < sourceIndices.Length; index++)
                {
                    indices.Add(sourceIndices[index] + vertexOffset);
                }
            }

            if (group.Mesh != null)
            {
                DestroyObject(group.Mesh);
            }

            Mesh combinedMesh = new Mesh
            {
                name = "VAT_RuntimeCombined_" + sourceMesh.name,
                indexFormat = totalVertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            combinedMesh.MarkDynamic();
            combinedMesh.SetVertices(vertices);
            combinedMesh.SetNormals(normals);
            combinedMesh.SetUVs(0, uv);
            combinedMesh.SetUVs(1, vatUv);
            combinedMesh.SetUVs(2, transform0);
            combinedMesh.SetUVs(3, transform1);
            combinedMesh.SetUVs(4, transform2);
            combinedMesh.SetUVs(5, frames);
            combinedMesh.SetIndices(indices.ToArray(), MeshTopology.Triangles, 0, false);
            combinedMesh.bounds = new Bounds(Vector3.zero, Vector3.one);

            group.Mesh = combinedMesh;
            group.MeshFilter.sharedMesh = combinedMesh;
            group.BuiltSourceCount = sourceCount;
        }

        private static void UpdateGroupData(BatchGroup group)
        {
            Source first = group.Sources[0];
            int sourceVertexCount = first.Mesh.vertexCount;
            Bounds bounds = new Bounds();
            bool hasBounds = false;

            for (int sourceIndex = 0; sourceIndex < group.Sources.Count; sourceIndex++)
            {
                Source source = group.Sources[sourceIndex];
                // VAT vertices and bounds are baked in the owning Body/VAT
                // root's local space. A sub-renderer's own local transform is
                // not part of that coordinate system and would apply scale or
                // offsets a second time when the entity parent is scaled.
                Matrix4x4 matrix = source.Owner != null
                    ? source.Owner.transform.localToWorldMatrix
                    : source.Renderer.localToWorldMatrix;
                Matrix4x4 normalMatrix = matrix.inverse.transpose;
                Vector4 row0 = new Vector4(matrix.m00, matrix.m01, matrix.m02, matrix.m03);
                Vector4 row1 = new Vector4(matrix.m10, matrix.m11, matrix.m12, matrix.m13);
                Vector4 row2 = new Vector4(matrix.m20, matrix.m21, matrix.m22, matrix.m23);
                Vector4 frame = new Vector4(source.FrameLower, source.FrameUpper, source.BlendWeight, 0f);
                int vertexOffset = sourceIndex * sourceVertexCount;

                for (int vertexIndex = 0; vertexIndex < sourceVertexCount; vertexIndex++)
                {
                    int combinedIndex = vertexOffset + vertexIndex;
                    group.Transform0[combinedIndex] = row0;
                    group.Transform1[combinedIndex] = row1;
                    group.Transform2[combinedIndex] = row2;
                    group.Frames[combinedIndex] = frame;

                    Vector3 normal = normalMatrix.MultiplyVector(group.BaseNormals[vertexIndex]);
                    group.Normals[combinedIndex] = normal.sqrMagnitude > 0.000001f
                        ? normal.normalized
                        : Vector3.up;
                }

                EncapsulateTransformedBounds(
                    ref bounds,
                    ref hasBounds,
                    source.BoundsMin,
                    source.BoundsMax,
                    matrix);
            }

            group.Mesh.SetNormals(group.Normals);
            group.Mesh.SetUVs(2, group.Transform0);
            group.Mesh.SetUVs(3, group.Transform1);
            group.Mesh.SetUVs(4, group.Transform2);
            group.Mesh.SetUVs(5, group.Frames);
            if (hasBounds)
            {
                group.Mesh.bounds = bounds;
            }
        }

        private static void EncapsulateTransformedBounds(
            ref Bounds bounds,
            ref bool hasBounds,
            Vector3 min,
            Vector3 max,
            Matrix4x4 matrix)
        {
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = new Vector3(
                    (corner & 1) == 0 ? min.x : max.x,
                    (corner & 2) == 0 ? min.y : max.y,
                    (corner & 4) == 0 ? min.z : max.z);
                Vector3 world = matrix.MultiplyPoint3x4(local);
                if (!hasBounds)
                {
                    bounds = new Bounds(world, Vector3.zero);
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(world);
                }
            }
        }

        private static void DestroyGroup(BatchGroup group)
        {
            if (group.Renderer != null) group.Renderer.enabled = false;
            if (group.Mesh != null) DestroyObject(group.Mesh);
            if (group.GameObject != null) DestroyObject(group.GameObject);
            group.Mesh = null;
            group.GameObject = null;
            group.MeshFilter = null;
            group.Renderer = null;
        }

        private static void DestroyObject(UnityEngine.Object target)
        {
            if (target == null) return;

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
