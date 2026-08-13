using System;
using System.Collections.Generic;
using GamePlay.HealthSystems;
using GamePlay.Items;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GamePlay.Map
{
    [DisallowMultipleComponent]
    public class ContentDataSceneTracker : MonoBehaviour
    {
        private enum CaptureMode
        {
            ReplaceContentFromScene,
            UpdateLinkedEntries
        }

        [Header("Source")]
        [SerializeField] private ContentDataSO targetContentData;
        [SerializeField] private Transform contentRoot;

        [Header("Capture")]
        [SerializeField] private CaptureMode captureMode = CaptureMode.ReplaceContentFromScene;
        [SerializeField] private bool includeInactive = true;
        [SerializeField] private bool sortByPosition = true;
        [SerializeField] private bool keepExistingOverridesWhenMatched = true;
        [SerializeField] private bool captureKnownOverridesFromScene = true;
        [SerializeField] private bool createBackupBeforeOverwrite = true;
        [SerializeField] private bool allowPartialCapture;
        [SerializeField] private bool resolveLooseSceneObjectsByName = true;
        [SerializeField] private bool applyPrefabInstanceOverrides;
        [SerializeField] private float matchPositionTolerance = 0.25f;
        [SerializeField] private string newAssetPath = "Assets/_Game/Playables/Era 1_T2/Content_FromScene.asset";

        private readonly List<ItemUnit> _items = new List<ItemUnit>(512);

#if UNITY_EDITOR
        private static readonly Dictionary<string, ItemUnit> s_prefabNameCache = new Dictionary<string, ItemUnit>(StringComparer.OrdinalIgnoreCase);

        [ContextMenu("Capture Scene To Target ContentDataSO")]
        public void CaptureSceneToTarget()
        {
            if (targetContentData == null)
            {
                Debug.LogWarning("[ContentDataSceneTracker] Missing target ContentDataSO.", this);
                return;
            }

            CaptureInto(targetContentData, captureMode);
        }

        [ContextMenu("Create New ContentDataSO From Scene")]
        public void CreateNewContentFromScene()
        {
            string path = AssetDatabase.GenerateUniqueAssetPath(newAssetPath);
            var content = ScriptableObject.CreateInstance<ContentDataSO>();
            content.ContentName = System.IO.Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(content, path);
            targetContentData = content;

            CaptureInto(content, CaptureMode.ReplaceContentFromScene);
            Selection.activeObject = content;
        }

        [ContextMenu("Refresh Linkers From Target ContentDataSO")]
        public void RefreshLinkersFromTarget()
        {
            if (targetContentData == null)
            {
                Debug.LogWarning("[ContentDataSceneTracker] Missing target ContentDataSO.", this);
                return;
            }

            Transform root = contentRoot != null ? contentRoot : transform;
            CollectItems(root);

            for (int i = 0; i < _items.Count; i++)
            {
                ItemUnit item = _items[i];
                if (item == null) continue;

                SpawnableObject matched = FindBestExistingMatch(targetContentData, item, root.position, out int matchedIndex);
                if (matched == null || matchedIndex < 0)
                {
                    continue;
                }

                var linker = item.GetComponent<ContentDataLinker>();
                if (linker == null)
                {
                    linker = item.gameObject.AddComponent<ContentDataLinker>();
                }

                linker.Link(targetContentData, matchedIndex, matched, item);
                EditorUtility.SetDirty(linker);
            }

            Debug.Log($"[ContentDataSceneTracker] Refreshed content linkers for {_items.Count} scene objects.", this);
        }

        private void CaptureInto(ContentDataSO content, CaptureMode mode)
        {
            if (content == null) return;

            Transform root = contentRoot != null ? contentRoot : transform;
            CollectItems(root);

            var capturedEntries = new List<SpawnableObject>(_items.Count);
            var capturedItems = new List<ItemUnit>(_items.Count);
            int skippedCount = 0;

            if (mode == CaptureMode.ReplaceContentFromScene)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    SpawnableObject captured = CaptureSpawnable(_items[i], content, -1, root.position);
                    if (captured != null)
                    {
                        capturedEntries.Add(captured);
                        capturedItems.Add(_items[i]);
                    }
                    else
                    {
                        skippedCount++;
                    }
                }

                if (!ValidateCaptureResult(content, capturedEntries.Count, skippedCount, replacingContent: true))
                {
                    return;
                }

                CreateBackupIfNeeded(content);
                Undo.RecordObject(content, "Capture ContentData From Scene");
                content.SpawnableObjects.Clear();

                for (int i = 0; i < capturedEntries.Count; i++)
                {
                    content.SpawnableObjects.Add(capturedEntries[i]);
                    LinkItem(capturedItems[i], content, content.SpawnableObjects.Count - 1, capturedEntries[i]);
                }
            }
            else
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    ItemUnit item = _items[i];
                    if (item == null) continue;

                    SpawnableObject matched = ResolveExistingEntry(content, item, root.position, out int matchedIndex);
                    SpawnableObject captured = CaptureSpawnable(item, content, matchedIndex, root.position);
                    if (captured == null)
                    {
                        skippedCount++;
                        continue;
                    }

                    capturedEntries.Add(captured);
                    capturedItems.Add(item);
                }

                if (!ValidateCaptureResult(content, capturedEntries.Count, skippedCount, replacingContent: false))
                {
                    return;
                }

                CreateBackupIfNeeded(content);
                Undo.RecordObject(content, "Capture ContentData From Scene");

                for (int i = 0; i < capturedEntries.Count; i++)
                {
                    ItemUnit item = capturedItems[i];
                    SpawnableObject captured = capturedEntries[i];
                    SpawnableObject matched = ResolveExistingEntry(content, item, root.position, out int matchedIndex);

                    if (matched != null && matchedIndex >= 0)
                    {
                        content.SpawnableObjects[matchedIndex] = captured;
                        LinkItem(item, content, matchedIndex, captured);
                    }
                    else
                    {
                        content.SpawnableObjects.Add(captured);
                        LinkItem(item, content, content.SpawnableObjects.Count - 1, captured);
                    }
                }
            }

            if (sortByPosition)
            {
                content.SortByPosition();
            }
            else
            {
                EditorUtility.SetDirty(content);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[ContentDataSceneTracker] Captured {capturedEntries.Count}/{_items.Count} scene objects into {content.name}. Skipped: {skippedCount}. Spawnable count: {content.SpawnableObjects.Count}.", content);
        }

        private bool ValidateCaptureResult(ContentDataSO content, int capturedCount, int skippedCount, bool replacingContent)
        {
            if (capturedCount <= 0)
            {
                Debug.LogError($"[ContentDataSceneTracker] Capture aborted. No valid SpawnableObject was resolved from {_items.Count} scene objects. The ContentDataSO was NOT modified.", this);
                return false;
            }

            if (!allowPartialCapture && skippedCount > 0)
            {
                Debug.LogError($"[ContentDataSceneTracker] Capture aborted. {skippedCount}/{_items.Count} scene objects could not resolve a prefab. Enable allowPartialCapture only if you intentionally want to skip them. The ContentDataSO was NOT modified.", this);
                return false;
            }

            if (replacingContent && content != null && content.SpawnableObjects != null && content.SpawnableObjects.Count > 0)
            {
                int oldCount = content.SpawnableObjects.Count;
                if (!allowPartialCapture && capturedCount < oldCount)
                {
                    Debug.LogError($"[ContentDataSceneTracker] Capture aborted. Replace mode captured {capturedCount} entries, less than existing SO count {oldCount}. Enable allowPartialCapture only if this cleanup is intentional. The ContentDataSO was NOT modified.", this);
                    return false;
                }
            }

            return true;
        }

        private void CreateBackupIfNeeded(ContentDataSO content)
        {
            if (!createBackupBeforeOverwrite || content == null)
            {
                return;
            }

            string sourcePath = AssetDatabase.GetAssetPath(content);
            if (string.IsNullOrEmpty(sourcePath))
            {
                return;
            }

            string folder = System.IO.Path.GetDirectoryName(sourcePath)?.Replace('\\', '/');
            string name = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
            string backupPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}_BackupBeforeTracker.asset");
            if (AssetDatabase.CopyAsset(sourcePath, backupPath))
            {
                Debug.Log($"[ContentDataSceneTracker] Backup created: {backupPath}", content);
            }
            else
            {
                Debug.LogWarning($"[ContentDataSceneTracker] Could not create backup for {sourcePath}.", content);
            }
        }

        private SpawnableObject CaptureSpawnable(ItemUnit item, ContentDataSO content, int matchedIndex, Vector3 basePosition)
        {
            if (item == null) return null;

            if (applyPrefabInstanceOverrides)
            {
                TryApplyPrefabOverrides(item);
            }

            ItemUnit prefab = ResolvePrefab(item);
            if (prefab == null)
            {
                Debug.LogWarning($"[ContentDataSceneTracker] Cannot resolve prefab for {item.name}. Skipped.", item);
                return null;
            }

            Vector3 worldOffset = item.transform.position - basePosition;
            var spawnable = new SpawnableObject
            {
                Prefab = prefab,
                PositionOnMap = worldOffset.z,
                PositionOffset = new Vector3(worldOffset.x, worldOffset.y, 0f),
                Rotation = item.transform.eulerAngles,
                Scale = item.transform.localScale
            };

            SpawnableObject existing = IsValidIndex(content, matchedIndex)
                ? content.SpawnableObjects[matchedIndex]
                : null;

            if (keepExistingOverridesWhenMatched && existing != null)
            {
                CloneOverrides(existing.propertyOverrides, spawnable.propertyOverrides);
                spawnable.overrideMaxHp = existing.overrideMaxHp;
                spawnable.maxHp = existing.maxHp;
            }

            if (captureKnownOverridesFromScene)
            {
                CaptureKnownOverrides(item, spawnable);
            }

            return spawnable;
        }

        private void CaptureKnownOverrides(ItemUnit item, SpawnableObject spawnable)
        {
            if (item == null || spawnable == null) return;

            CaptureHealthOverride(item, spawnable);
            CaptureFireGateOverride(item, spawnable);
            CaptureSoldierBallOverride(item, spawnable);
        }

        private void CaptureHealthOverride(ItemUnit item, SpawnableObject spawnable)
        {
            var health = item.GetComponentInChildren<HealthComponent>(true);
            if (health == null) return;

            spawnable.overrideMaxHp = true;
            spawnable.maxHp = Mathf.Max(1, health.MaxHealth);

            ReplaceOverride(spawnable.propertyOverrides, new HealthComponentOverride
            {
                overrideMaxHp = true,
                maxHp = spawnable.maxHp
            });
        }

        private void CaptureFireGateOverride(ItemUnit item, SpawnableObject spawnable)
        {
            StatModifierGate gate = item as StatModifierGate;
            if (gate == null || gate.Data == null) return;

            ReplaceOverride(spawnable.propertyOverrides, new FireGateOverride
            {
                overrideValue = true,
                Value = gate.Data.Value,
                Armor = gate.Data.Armor,
                LeftOffset = gate.LeftOffset,
                RightOffset = gate.RightOffset
            });
        }

        private void CaptureSoldierBallOverride(ItemUnit item, SpawnableObject spawnable)
        {
            SoldierBall soldierBall = item as SoldierBall;
            if (soldierBall == null || soldierBall.Data == null) return;

            ReplaceOverride(spawnable.propertyOverrides, new SoldierBallOverride
            {
                overrideValue = true,
                ChangeType = soldierBall.Data.ChangeType,
                Value = soldierBall.Data.Value,
                Level = soldierBall.Data.Level,
                LeftOffset = soldierBall.LeftOffset,
                RightOffset = soldierBall.RightOffset
            });
        }

        private void ReplaceOverride<T>(List<ItemUnitPropertyOverride> overrides, T replacement)
            where T : ItemUnitPropertyOverride
        {
            if (overrides == null || replacement == null) return;

            Type type = typeof(T);
            for (int i = overrides.Count - 1; i >= 0; i--)
            {
                if (overrides[i] != null && overrides[i].GetType() == type)
                {
                    overrides.RemoveAt(i);
                }
            }

            overrides.Add(replacement);
        }

        private static void CloneOverrides(List<ItemUnitPropertyOverride> source, List<ItemUnitPropertyOverride> destination)
        {
            if (source == null || destination == null) return;

            for (int i = 0; i < source.Count; i++)
            {
                ItemUnitPropertyOverride copy = CloneOverride(source[i]);
                if (copy != null)
                {
                    destination.Add(copy);
                }
            }
        }

        private static ItemUnitPropertyOverride CloneOverride(ItemUnitPropertyOverride source)
        {
            if (source == null) return null;

            Type type = source.GetType();
            string json = JsonUtility.ToJson(source);
            return JsonUtility.FromJson(json, type) as ItemUnitPropertyOverride;
        }

        private SpawnableObject ResolveExistingEntry(ContentDataSO content, ItemUnit item, Vector3 basePosition, out int index)
        {
            index = -1;
            if (content == null || item == null || content.SpawnableObjects == null)
            {
                return null;
            }

            var linker = item.GetComponent<ContentDataLinker>();
            if (linker != null && linker.SourceDataSO == content && IsValidIndex(content, linker.ItemIndex))
            {
                index = linker.ItemIndex;
                return content.SpawnableObjects[index];
            }

            return FindBestExistingMatch(content, item, basePosition, out index);
        }

        private SpawnableObject FindBestExistingMatch(ContentDataSO content, ItemUnit item, Vector3 basePosition, out int index)
        {
            index = -1;
            if (content == null || item == null || content.SpawnableObjects == null)
            {
                return null;
            }

            ItemUnit prefab = ResolvePrefab(item);
            Vector3 worldOffset = item.transform.position - basePosition;
            float bestDistance = float.MaxValue;
            SpawnableObject best = null;

            for (int i = 0; i < content.SpawnableObjects.Count; i++)
            {
                SpawnableObject candidate = content.SpawnableObjects[i];
                if (candidate == null || candidate.Prefab == null)
                {
                    continue;
                }

                if (prefab != null && candidate.Prefab != prefab)
                {
                    continue;
                }

                Vector3 candidateOffset = candidate.PositionOffset + Vector3.forward * candidate.PositionOnMap;
                float distance = Vector3.Distance(worldOffset, candidateOffset);
                if (distance > matchPositionTolerance || distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                best = candidate;
                index = i;
            }

            return best;
        }

        private static bool IsValidIndex(ContentDataSO content, int index)
        {
            return content != null &&
                   content.SpawnableObjects != null &&
                   index >= 0 &&
                   index < content.SpawnableObjects.Count;
        }

        private void CollectItems(Transform root)
        {
            _items.Clear();
            if (root == null) return;

            var items = root.GetComponentsInChildren<ItemUnit>(includeInactive);
            for (int i = 0; i < items.Length; i++)
            {
                ItemUnit item = items[i];
                if (item == null || item.transform == root) continue;
                _items.Add(item);
            }

            _items.Sort((a, b) =>
            {
                if (a == null && b == null) return 0;
                if (a == null) return 1;
                if (b == null) return -1;
                return a.transform.position.z.CompareTo(b.transform.position.z);
            });
        }

        private ItemUnit ResolvePrefab(ItemUnit sceneItem)
        {
            if (sceneItem == null) return null;

            var prefab = PrefabUtility.GetCorrespondingObjectFromSource(sceneItem);
            if (prefab != null) return prefab;

            var prefabRoot = PrefabUtility.GetCorrespondingObjectFromSource(sceneItem.gameObject);
            if (prefabRoot != null)
            {
                var prefabItem = prefabRoot.GetComponent<ItemUnit>();
                if (prefabItem != null) return prefabItem;
            }

            var linker = sceneItem.GetComponent<ContentDataLinker>();
            if (linker != null && linker.OriginalPrefab != null)
            {
                return linker.OriginalPrefab;
            }

            if (linker != null &&
                linker.SourceDataSO != null &&
                linker.ItemIndex >= 0 &&
                linker.ItemIndex < linker.SourceDataSO.SpawnableObjects.Count)
            {
                var linkedSpawnable = linker.SourceDataSO.SpawnableObjects[linker.ItemIndex];
                if (linkedSpawnable != null && linkedSpawnable.Prefab != null)
                {
                    return linkedSpawnable.Prefab;
                }
            }

            if (resolveLooseSceneObjectsByName)
            {
                ItemUnit nameMatchedPrefab = ResolvePrefabBySceneName(sceneItem);
                if (nameMatchedPrefab != null)
                {
                    return nameMatchedPrefab;
                }
            }

            return null;
        }

        private static ItemUnit ResolvePrefabBySceneName(ItemUnit sceneItem)
        {
            if (sceneItem == null) return null;

            string lookupName = NormalizeSceneObjectName(sceneItem.gameObject.name);
            if (string.IsNullOrEmpty(lookupName)) return null;

            if (s_prefabNameCache.TryGetValue(lookupName, out ItemUnit cached))
            {
                return cached;
            }

            string[] guids = AssetDatabase.FindAssets($"{lookupName} t:Prefab", new[] { "Assets" });
            ItemUnit best = null;
            int candidateCount = 0;

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path)) continue;

                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;

                var item = go.GetComponent<ItemUnit>() ?? go.GetComponentInChildren<ItemUnit>(true);
                if (item == null) continue;

                string prefabName = NormalizeSceneObjectName(go.name);
                string itemName = NormalizeSceneObjectName(item.gameObject.name);
                if (!string.Equals(prefabName, lookupName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(itemName, lookupName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidateCount++;
                if (best == null)
                {
                    best = item;
                }

                if (string.Equals(prefabName, lookupName, StringComparison.OrdinalIgnoreCase))
                {
                    best = item;
                    break;
                }
            }

            if (best != null)
            {
                s_prefabNameCache[lookupName] = best;
                if (candidateCount > 1)
                {
                    Debug.LogWarning($"[ContentDataSceneTracker] Multiple prefab candidates found for '{lookupName}'. Using '{AssetDatabase.GetAssetPath(best)}'.");
                }
            }
            else
            {
                s_prefabNameCache[lookupName] = null;
            }

            return best;
        }

        private static string NormalizeSceneObjectName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;

            string result = rawName.Trim();
            const string cloneSuffix = "(Clone)";
            if (result.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Substring(0, result.Length - cloneSuffix.Length).Trim();
            }

            return result;
        }

        private static void TryApplyPrefabOverrides(ItemUnit sceneItem)
        {
            if (sceneItem == null) return;

            GameObject root = PrefabUtility.GetNearestPrefabInstanceRoot(sceneItem.gameObject);
            if (root == null) return;

            PrefabUtility.ApplyPrefabInstance(root, InteractionMode.UserAction);
        }

        private static void LinkItem(ItemUnit item, ContentDataSO content, int index, SpawnableObject spawnable)
        {
            if (item == null || content == null || spawnable == null)
            {
                return;
            }

            var linker = item.GetComponent<ContentDataLinker>();
            if (linker == null)
            {
                linker = item.gameObject.AddComponent<ContentDataLinker>();
            }

            linker.Link(content, index, spawnable, item);
            EditorUtility.SetDirty(linker);
        }
#endif
    }
}
