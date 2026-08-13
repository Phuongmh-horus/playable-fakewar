using System.Collections.Generic;
using System.IO;
using GamePlay.Utilities.EraAssetSwapper;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace GamePlay.Editor.EraAssetSwapper
{
    public class EraAssetSwapperWindow : EditorWindow
    {
        public enum TargetMode
        {
            SinglePrefab,
            Folder
        }

        private EraMappingProfile mappingProfile;
        private TargetMode targetMode = TargetMode.SinglePrefab;

        private GameObject targetPrefab;
        private DefaultAsset targetFolder;

        [MenuItem("Tools/Age Evolution/Era Asset Swapper")]
        public static void ShowWindow()
        {
            var window = GetWindow<EraAssetSwapperWindow>("Era Asset Swapper");
            window.minSize = new Vector2(400, 400);
            window.Show();
        }

        private void OnGUI()
        {
            GUILayout.Label("Era Asset Swapper", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            // ZONE 1: DATABASE
            GUILayout.Label("Zone 1: Database", EditorStyles.boldLabel);
            mappingProfile = (EraMappingProfile)EditorGUILayout.ObjectField("Era Mapping Data", mappingProfile, typeof(EraMappingProfile), false);

            if (mappingProfile == null)
            {
                EditorGUILayout.HelpBox("Hãy chọn Era Mapping Profile để tiếp tục.", MessageType.Warning);
                return;
            }

            EditorGUILayout.Space();

            // ZONE 2: TARGETS
            GUILayout.Label("Zone 2: Targets", EditorStyles.boldLabel);
            targetMode = (TargetMode)EditorGUILayout.EnumPopup("Target Mode", targetMode);

            switch (targetMode)
            {
                case TargetMode.SinglePrefab:
                    targetPrefab = (GameObject)EditorGUILayout.ObjectField("Target Prefab", targetPrefab, typeof(GameObject), false);
                    break;
                case TargetMode.Folder:
                    targetFolder = (DefaultAsset)EditorGUILayout.ObjectField("Target Folder", targetFolder, typeof(DefaultAsset), false);
                    break;
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Destructive Swap: Tool sẽ hoán đổi asset con và ghi đè trực tiếp lên Prefab gốc. Sau khi build Playable, hãy revert qua Git nếu cần thiết.", MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ZONE 3: ACTIONS
            GUILayout.Label("Zone 3: Actions", EditorStyles.boldLabel);
            
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan & Preview", GUILayout.Height(30)))
            {
                ExecuteSwap(true);
            }

            GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
            if (GUILayout.Button("Destructive Swap!", GUILayout.Height(30)))
            {
                if (EditorUtility.DisplayDialog("Cảnh Báo", "Bạn đang thực hiện Destructive Swap (ghi đè trực tiếp). Bạn có chắc chắn chưa?", "Xác nhận", "Hủy"))
                {
                    ExecuteSwap(false);
                }
            }
            GUI.backgroundColor = Color.white;
            GUILayout.EndHorizontal();
        }

        private void ExecuteSwap(bool isPreview)
        {
            if (targetMode == TargetMode.SinglePrefab && targetPrefab != null)
            {
                ProcessPrefab(targetPrefab, isPreview);
            }
            else if (targetMode == TargetMode.Folder && targetFolder != null)
            {
                string folderPath = AssetDatabase.GetAssetPath(targetFolder);
                string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { folderPath });
                foreach (string guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (prefab != null) ProcessPrefab(prefab, isPreview);
                }
            }
            else
            {
                Debug.LogWarning("[Era Asset Swapper] Target chưa được chọn hợp lệ!");
                return;
            }

            if (!isPreview)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log("<color=green>[Era Asset Swapper] Đã hoàn thành quá trình swap!</color>");
            }
            else
            {
                Debug.Log("<color=yellow>[Era Asset Swapper] Chế độ Scan & Preview đã chạy xong.</color>");
            }
        }

        private void ProcessPrefab(GameObject prefab, bool isPreview)
        {
            string originalPath = AssetDatabase.GetAssetPath(prefab);

            if (isPreview)
            {
                Debug.Log($"[Preview] Sẽ quét qua Prefab: {prefab.name}");
                return;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            int changesCount = SwapAssetsInHierarchy(instance.transform);

            if (changesCount > 0)
            {
                PrefabUtility.SaveAsPrefabAsset(instance, originalPath);
                Debug.Log($"Đã swap {changesCount} asset con bên trong Prefab gốc: {Path.GetFileName(originalPath)}");
            }

            DestroyImmediate(instance);
        }

        private int SwapAssetsInHierarchy(Transform root)
        {
            int changes = 0;

            // MeshFilter
            var meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
            foreach (var mf in meshFilters)
            {
                if (mf.sharedMesh != null)
                {
                    Mesh newMesh = mappingProfile.GetReplacement(mf.sharedMesh) as Mesh;
                    if (newMesh != null)
                    {
                        Undo.RecordObject(mf, "Swap Mesh");
                        mf.sharedMesh = newMesh;
                        changes++;
                    }
                }
            }

            // MeshRenderer (Materials)
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (var ren in renderers)
            {
                var sharedMats = ren.sharedMaterials;
                bool matChanged = false;
                for (int i = 0; i < sharedMats.Length; i++)
                {
                    if (sharedMats[i] != null)
                    {
                        Material newMat = mappingProfile.GetReplacement(sharedMats[i]) as Material;
                        if (newMat != null)
                        {
                            sharedMats[i] = newMat;
                            matChanged = true;
                            changes++;
                        }
                    }
                }
                if (matChanged)
                {
                    Undo.RecordObject(ren, "Swap Material");
                    ren.sharedMaterials = sharedMats;
                }
            }

            // SpriteRenderer
            var spriteRenderers = root.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var sr in spriteRenderers)
            {
                if (sr.sprite != null)
                {
                    Sprite newSprite = mappingProfile.GetReplacement(sr.sprite) as Sprite;
                    if (newSprite != null)
                    {
                        Undo.RecordObject(sr, "Swap Sprite");
                        sr.sprite = newSprite;
                        changes++;
                    }
                }
            }

            // UI Image
            var images = root.GetComponentsInChildren<Image>(true);
            foreach (var img in images)
            {
                if (img.sprite != null)
                {
                    Sprite newSprite = mappingProfile.GetReplacement(img.sprite) as Sprite;
                    if (newSprite != null)
                    {
                        Undo.RecordObject(img, "Swap UI Sprite");
                        img.sprite = newSprite;
                        changes++;
                    }
                }
            }

            // AudioSource
            var audioSources = root.GetComponentsInChildren<AudioSource>(true);
            foreach (var src in audioSources)
            {
                if (src.clip != null)
                {
                    AudioClip newClip = mappingProfile.GetReplacement(src.clip) as AudioClip;
                    if (newClip != null)
                    {
                        Undo.RecordObject(src, "Swap AudioClip");
                        src.clip = newClip;
                        changes++;
                    }
                }
            }

            // Animator
            var animators = root.GetComponentsInChildren<Animator>(true);
            foreach (var anim in animators)
            {
                if (anim.runtimeAnimatorController != null)
                {
                    RuntimeAnimatorController newController = mappingProfile.GetReplacement(anim.runtimeAnimatorController) as RuntimeAnimatorController;
                    if (newController != null)
                    {
                        Undo.RecordObject(anim, "Swap Animator Controller");
                        anim.runtimeAnimatorController = newController;
                        changes++;
                    }
                }

                if (anim.avatar != null)
                {
                    Avatar newAvatar = mappingProfile.GetReplacement(anim.avatar) as Avatar;
                    if (newAvatar != null)
                    {
                        Undo.RecordObject(anim, "Swap Avatar");
                        anim.avatar = newAvatar;
                        changes++;
                    }
                }
            }

            return changes;
        }
    }
}
