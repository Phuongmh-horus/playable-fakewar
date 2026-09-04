using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace OptimizedFeature.Scripts.Editor
{
    /// <summary>
    /// Shared editor-only runtime setup operation for baked VAT characters.
    /// The UI is hosted exclusively by VATBakeToolWindow's Runtime Setup tab.
    /// </summary>
    public static class VATSetupHelper
    {
        /// <summary>
        /// Shared VAT setup operation used by VATBakeToolWindow's Runtime Setup tab.
        /// </summary>
        public static void SetupAllVATCharacters(
            IList<GameObject> targetRoots,
            VATAssetDataSO vatAssetData,
            Material vatMaterial)
        {
            SetupAllVATCharacters(targetRoots, vatAssetData, vatMaterial, -1);
        }

        /// <summary>
        /// Configures the body and at most one named item channel. Set
        /// defaultWeaponIndex to -1 to explicitly skip item setup. The
        /// parameter name is retained for source compatibility.
        /// </summary>
        public static void SetupAllVATCharacters(
            IList<GameObject> targetRoots,
            VATAssetDataSO vatAssetData,
            Material vatMaterial,
            int defaultWeaponIndex)
        {
            if (vatAssetData == null || targetRoots == null) return;

            List<Material> targetMaterials = new List<Material>();
            if (vatMaterial != null)
            {
                targetMaterials.Add(vatMaterial);
            }
            else if (vatAssetData.BakedMaterials != null && vatAssetData.BakedMaterials.Count > 0)
            {
                targetMaterials.AddRange(vatAssetData.BakedMaterials);
            }

            if (targetMaterials.Count == 0)
            {
                Debug.LogError("[VATSetupHelper] No materials specified and no baked materials found in VATAssetDataSO!");
                return;
            }

            // Ensure VATSystem exists in the scene
            VATSystem system = UnityEngine.Object.FindObjectOfType<VATSystem>();
            if (system == null)
            {
                GameObject systemGo = new GameObject("VATSystem");
                system = systemGo.AddComponent<VATSystem>();
                Debug.Log("[VATSetupHelper] Created VATSystem Manager in the scene.");
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Recreate VAT Runtime Setup");

            int successCount = 0;
            int partialCount = 0;
            try
            {
                for (int i = 0; i < targetRoots.Count; i++)
                {
                    GameObject target = targetRoots[i];
                    if (target == null) continue;

                    if (SetupSingleCharacter(
                            target,
                            vatAssetData,
                            targetMaterials,
                            defaultWeaponIndex))
                    {
                        successCount++;
                    }
                    else
                    {
                        partialCount++;
                    }
                }
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }

            if (partialCount > 0)
            {
                Debug.LogWarning(
                    $"[VATSetupHelper] Recreated {successCount} VAT character(s). " +
                    $"{partialCount} character(s) were recreated without the selected item because its baked frame manifest is invalid or does not match the body.");
            }
            else
            {
                Debug.Log($"[VATSetupHelper] Successfully recreated {successCount} VAT character(s)!");
            }
        }

        private static bool SetupSingleCharacter(
            GameObject target,
            VATAssetDataSO vatAssetData,
            List<Material> targetMaterials,
            int defaultWeaponIndex)
        {
            Undo.RegisterFullObjectHierarchyUndo(target, "Recreate VAT Character");

            Vector3 storedModelScale = IsUsableModelScale(vatAssetData.ModelScale)
                ? vatAssetData.ModelScale
                : Vector3.one;
            Undo.RecordObject(target.transform, "Apply VAT Model Scale");
            target.transform.localScale = storedModelScale;

            VAT_RenderComponent renderComponent = target.GetComponent<VAT_RenderComponent>();
            RemoveExistingVATRuntimeComponents(target, renderComponent);

            // A dependent component such as VATTestingController can require the
            // root VAT_RenderComponent, which Unity then forbids us to remove.
            // In that case clear all old runtime bindings and reuse that required
            // component. The binding is still recreated entirely from the new
            // source data below.
            if (renderComponent == null)
            {
                renderComponent = Undo.AddComponent<VAT_RenderComponent>(target);
            }
            else
            {
                ResetRootVATRenderer(renderComponent);
            }

            // Setters perform the normal runtime binding path. Recording the
            // component first makes the references/material setup undoable too.
            Undo.RecordObject(renderComponent, "Configure Recreated VAT Renderer");
            renderComponent.SetMaterials(targetMaterials.ToArray());
            renderComponent.SetVATAssetData(vatAssetData);
            EditorUtility.SetDirty(renderComponent);

            // Runtime Setup intentionally loads one configured item channel
            // only. Additional item assets remain available for switching.
            bool weaponSetupSucceeded = SetupWeaponSubRenders(
                target,
                renderComponent,
                vatAssetData,
                defaultWeaponIndex);

            // 3. Register with VATSystem
            VATSystem.RegisterAnimator(renderComponent);

            EditorUtility.SetDirty(target);

            // Keep prefab-instance changes as overrides. Applying automatically
            // would mutate the prefab source outside this recreate Undo group.
            if (PrefabUtility.IsPartOfPrefabInstance(target))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                Debug.Log(
                    $"[VATSetupHelper] Recreated prefab instance override: {target.name}. " +
                    "Review it, then Apply manually if the recreated setup should become the prefab default.");
            }

            Debug.Log($"[VATSetupHelper] Recreated: {target.name}");
            return weaponSetupSucceeded;
        }

        private static bool IsUsableModelScale(Vector3 scale)
        {
            return !float.IsNaN(scale.x) && !float.IsInfinity(scale.x) &&
                   !float.IsNaN(scale.y) && !float.IsInfinity(scale.y) &&
                   !float.IsNaN(scale.z) && !float.IsInfinity(scale.z) &&
                   Mathf.Abs(scale.x) > 0.000001f &&
                   Mathf.Abs(scale.y) > 0.000001f &&
                   Mathf.Abs(scale.z) > 0.000001f;
        }

        /// <summary>
        /// Removes VAT runtime state only. Baked VAT assets are never altered by
        /// Runtime Setup; they remain the source used to create the new scene
        /// components below.
        /// </summary>
        private static void RemoveExistingVATRuntimeComponents(
            GameObject target,
            VAT_RenderComponent rootRenderer)
        {
            // Generated sub-renders are disposable runtime output. Remove the
            // whole GameObject before scanning components so stale objects are
            // also cleaned when their VAT component was already lost or when
            // they were created by an older version of the tool.
            RemoveGeneratedVATChildObjects(target);

            VATWeaponRenderComponent[] weaponComponents =
                target.GetComponentsInChildren<VATWeaponRenderComponent>(true);
            for (int i = 0; i < weaponComponents.Length; i++)
            {
                VATWeaponRenderComponent weaponComponent = weaponComponents[i];
                if (weaponComponent == null) continue;

                ClearVATRendererBinding(weaponComponent.gameObject);
                Undo.DestroyObjectImmediate(weaponComponent);
            }

            VAT_RenderComponent[] bodyComponents =
                target.GetComponentsInChildren<VAT_RenderComponent>(true);
            for (int i = 0; i < bodyComponents.Length; i++)
            {
                VAT_RenderComponent bodyComponent = bodyComponents[i];
                if (bodyComponent == null) continue;

                // Never destroy the root renderer selected for recreation. It
                // may be required by another component on the same GameObject.
                if (bodyComponent == rootRenderer)
                {
                    continue;
                }

                ClearVATRendererBinding(bodyComponent.gameObject);
                Undo.DestroyObjectImmediate(bodyComponent);
            }
        }

        private static void RemoveGeneratedVATChildObjects(GameObject target)
        {
            Transform[] children = target.GetComponentsInChildren<Transform>(true);
            List<GameObject> generatedObjects = new List<GameObject>();

            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (!IsGeneratedVATChildObject(child))
                {
                    continue;
                }

                // Destroy only the highest generated object in a chain. This
                // keeps the cleanup safe if a generated object contains stale
                // generated descendants from a previous setup.
                bool hasGeneratedParent = false;
                Transform parent = child.parent;
                while (parent != null && parent != target.transform)
                {
                    if (IsGeneratedVATChildObject(parent))
                    {
                        hasGeneratedParent = true;
                        break;
                    }

                    parent = parent.parent;
                }

                if (!hasGeneratedParent)
                {
                    generatedObjects.Add(child.gameObject);
                }
            }

            for (int i = 0; i < generatedObjects.Count; i++)
            {
                GameObject generatedObject = generatedObjects[i];
                if (generatedObject != null)
                {
                    Undo.DestroyObjectImmediate(generatedObject);
                }
            }
        }

        private static bool IsGeneratedVATChildObject(Transform transform)
        {
            if (transform == null || transform.parent == null)
            {
                return false;
            }

            return transform.name == "MeshRenderer_VAT" ||
                   transform.name.StartsWith("VAT_Item_SubRender_", StringComparison.Ordinal) ||
                   transform.name.StartsWith("VAT_Weapon_SubRender_", StringComparison.Ordinal);
        }

        private static void ResetRootVATRenderer(VAT_RenderComponent renderComponent)
        {
            Undo.RecordObject(renderComponent, "Clear Previous VAT Renderer Data");
            ClearVATRendererBinding(renderComponent.gameObject);
            renderComponent.SetVATAssetData(null);
            renderComponent.SetMaterials(new Material[0]);
            renderComponent.enabled = true;
            EditorUtility.SetDirty(renderComponent);
        }

        private static void ClearVATRendererBinding(GameObject target)
        {
            MeshFilter meshFilter = target.GetComponent<MeshFilter>();
            if (meshFilter != null)
            {
                Undo.RecordObject(meshFilter, "Clear Previous VAT Mesh");
                meshFilter.sharedMesh = null;
                EditorUtility.SetDirty(meshFilter);
            }

            MeshRenderer meshRenderer = target.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                Undo.RecordObject(meshRenderer, "Clear Previous VAT Renderer");
                meshRenderer.sharedMaterials = new Material[0];
                meshRenderer.enabled = false;
                EditorUtility.SetDirty(meshRenderer);
            }
        }

        private static bool SetupWeaponSubRenders(
            GameObject target,
            VAT_RenderComponent renderComponent,
            VATAssetDataSO vatAssetData,
            int defaultWeaponIndex)
        {
            VATWeaponAssetEntry selectedEntry = GetWeaponAssetEntry(vatAssetData, defaultWeaponIndex);
            VATWeaponRenderComponent[] existingRenders =
                target.GetComponentsInChildren<VATWeaponRenderComponent>(true);

            // -1 explicitly means no item should be instantiated or loaded.
            if (selectedEntry == null || selectedEntry.WeaponAsset == null)
            {
                for (int i = 0; i < existingRenders.Length; i++)
                {
                    VATWeaponRenderComponent existingRender = existingRenders[i];
                    if (existingRender != null)
                    {
                        existingRender.SetFrameSource(renderComponent);
                        existingRender.SetWeaponAsset(null);
                    }
                }
                renderComponent.RefreshWeaponSubRenders(false);
                return true;
            }

            if (!renderComponent.IsWeaponFrameManifestCompatible(selectedEntry.WeaponAsset))
            {
                Debug.LogError(
                    $"[VATSetupHelper] Item VAT '{selectedEntry.WeaponAsset.name}' was not recreated for " +
                    $"'{target.name}' because it does not share the selected Body VAT frame manifest.",
                    target);
                return false;
            }

            string weaponName = string.IsNullOrWhiteSpace(selectedEntry.WeaponName)
                ? "Item"
                : selectedEntry.WeaponName.Trim();
            VATWeaponRenderComponent weaponRender = FindWeaponRender(existingRenders, selectedEntry.WeaponHash);
            if (weaponRender == null && existingRenders.Length > 0)
            {
                weaponRender = existingRenders[0];
            }

            if (weaponRender == null)
            {
                GameObject weaponRenderObject = new GameObject("VAT_Item_SubRender_" + weaponName);
                Undo.RegisterCreatedObjectUndo(weaponRenderObject, "Create VAT Item Sub-render");
                weaponRenderObject.transform.SetParent(target.transform, false);
                weaponRender = Undo.AddComponent<VATWeaponRenderComponent>(weaponRenderObject);
            }

            // Item VAT vertices are baked in the selected VAT target-root
            // space. The runtime sub-render must therefore be a neutral child
            // of that same root; otherwise its old source transform is applied
            // a second time when the target parent is scaled.
            Undo.RecordObject(weaponRender.transform, "Normalize VAT Item Sub-render Transform");
            weaponRender.transform.SetParent(target.transform, false);
            weaponRender.transform.localPosition = Vector3.zero;
            weaponRender.transform.localRotation = Quaternion.identity;
            weaponRender.transform.localScale = Vector3.one;

            Undo.RecordObject(weaponRender, "Configure Recreated VAT Item");
            weaponRender.SetWeaponHash(selectedEntry.WeaponHash);
            weaponRender.SetFrameSource(renderComponent);
            weaponRender.SetWeaponAsset(selectedEntry.WeaponAsset);
            EditorUtility.SetDirty(weaponRender);

            // Keep pre-existing sub-renders inactive so Runtime Setup never
            // displays more than the explicitly selected default weapon.
            for (int i = 0; i < existingRenders.Length; i++)
            {
                VATWeaponRenderComponent existingRender = existingRenders[i];
                if (existingRender != null && existingRender != weaponRender)
                {
                    existingRender.SetFrameSource(renderComponent);
                    existingRender.SetWeaponAsset(null);
                }
            }

            renderComponent.RefreshWeaponSubRenders(false);
            return true;
        }

        private static VATWeaponRenderComponent FindWeaponRender(
            VATWeaponRenderComponent[] existingRenders,
            int weaponHash)
        {
            for (int i = 0; i < existingRenders.Length; i++)
            {
                VATWeaponRenderComponent render = existingRenders[i];
                if (render != null && render.WeaponHash == weaponHash)
                {
                    return render;
                }
            }

            return null;
        }

        private static VATWeaponAssetEntry GetWeaponAssetEntry(VATAssetDataSO vatAssetData, int weaponIndex)
        {
            if (vatAssetData == null || weaponIndex < 0)
            {
                return null;
            }

            if (vatAssetData.WeaponAssets != null && weaponIndex < vatAssetData.WeaponAssets.Count)
            {
                return vatAssetData.WeaponAssets[weaponIndex];
            }

            return weaponIndex == 0 && vatAssetData.DefaultWeaponAsset != null
                ? new VATWeaponAssetEntry
                {
                    WeaponName = "Item",
                    WeaponHash = VATWeaponAssetEntry.DefaultItemHash,
                    WeaponAsset = vatAssetData.DefaultWeaponAsset
                }
                : null;
        }
    }
}
