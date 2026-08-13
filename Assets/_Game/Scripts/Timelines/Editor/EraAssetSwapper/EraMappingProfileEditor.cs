using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using GamePlay.Utilities.EraAssetSwapper;

namespace GamePlay.Editor.EraAssetSwapper
{
    [CustomEditor(typeof(EraMappingProfile))]
    public class EraMappingProfileEditor : UnityEditor.Editor
    {
        private DefaultAsset originalFolder;
        private DefaultAsset replacementFolder;
        private string originalSuffix = "_t1_a1";
        private string replacementSuffix = "_t1_a2";

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Auto Match Tool", EditorStyles.boldLabel);
            
            EditorGUILayout.HelpBox("Công cụ tự động map các asset cùng loại giữa 2 thư mục dựa vào hậu tố tên.\nVD: WeaponGate_t1_a1 -> WeaponGate_t1_a2", MessageType.Info);

            originalFolder = (DefaultAsset)EditorGUILayout.ObjectField("Original Folder", originalFolder, typeof(DefaultAsset), false);
            replacementFolder = (DefaultAsset)EditorGUILayout.ObjectField("Replacement Folder", replacementFolder, typeof(DefaultAsset), false);
            
            originalSuffix = EditorGUILayout.TextField("Original Suffix", originalSuffix);
            replacementSuffix = EditorGUILayout.TextField("Replacement Suffix", replacementSuffix);

            EditorGUI.BeginDisabledGroup(originalFolder == null || replacementFolder == null);
            if (GUILayout.Button("Auto Match by Name", GUILayout.Height(30)))
            {
                AutoMatch();
            }
            EditorGUI.EndDisabledGroup();
        }

        private void AutoMatch()
        {
            EraMappingProfile profile = (EraMappingProfile)target;
            
            string originalPath = AssetDatabase.GetAssetPath(originalFolder);
            string replacementPath = AssetDatabase.GetAssetPath(replacementFolder);

            string[] originalGuids = AssetDatabase.FindAssets("", new[] { originalPath });
            string[] replacementGuids = AssetDatabase.FindAssets("", new[] { replacementPath });

            Dictionary<string, Object> replacementAssetsMap = new Dictionary<string, Object>();
            
            // Xây dựng từ điển các asset thay thế
            foreach (string guid in replacementGuids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (Directory.Exists(assetPath)) continue; // Skip folders
                
                Object asset = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (asset != null)
                {
                    string assetName = asset.name;
                    // Xoá hậu tố để lấy tên gốc
                    if (!string.IsNullOrEmpty(replacementSuffix) && assetName.EndsWith(replacementSuffix))
                    {
                        string baseName = assetName.Substring(0, assetName.Length - replacementSuffix.Length);
                        replacementAssetsMap[baseName] = asset;
                    }
                    else
                    {
                        // Lưu cả tên đầy đủ phòng trường hợp asset không có hậu tố nhưng trùng tên 100%
                        replacementAssetsMap[assetName] = asset;
                    }
                }
            }

            int matchCount = 0;
            Undo.RecordObject(profile, "Auto Match Era Assets");

            // Quét các asset gốc để nối map
            foreach (string guid in originalGuids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (Directory.Exists(assetPath)) continue;
                
                Object originalAsset = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (originalAsset != null)
                {
                    string originalName = originalAsset.name;
                    string baseName = originalName;
                    
                    if (!string.IsNullOrEmpty(originalSuffix) && originalName.EndsWith(originalSuffix))
                    {
                        baseName = originalName.Substring(0, originalName.Length - originalSuffix.Length);
                    }

                    // Tìm kiếm asset thay thế
                    if (replacementAssetsMap.TryGetValue(baseName, out Object replacementAsset))
                    {
                        // Kiểm tra xem đã map chưa để không bị trùng
                        bool alreadyMapped = false;
                        foreach (var pair in profile.mappings)
                        {
                            if (pair.originalAsset == originalAsset)
                            {
                                pair.replacementAsset = replacementAsset; // Cập nhật lại nếu đã có
                                alreadyMapped = true;
                                break;
                            }
                        }

                        if (!alreadyMapped)
                        {
                            profile.mappings.Add(new AssetPair
                            {
                                originalAsset = originalAsset,
                                replacementAsset = replacementAsset
                            });
                        }
                        
                        matchCount++;
                    }
                }
            }

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            
            Debug.Log($"[Era Asset Swapper] Auto Match completed. Found and mapped {matchCount} pairs.");
        }
    }
}
