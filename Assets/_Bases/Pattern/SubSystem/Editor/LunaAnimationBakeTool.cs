using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

#if UNITY_EDITOR
using UnityEditor.Animations;

public enum BakeMode
{
    ReplaceOriginal,
    CreateBakedClone
}

public class LunaAnimationBakeTool : EditorWindow
{
    [System.Serializable]
    private class BackupRecord
    {
        public string originalAssetPath;
        public string backupTempPath;
        public System.DateTime timestamp;
    }

    private List<Object> _targetAssets = new List<Object>();
    private BakeMode _bakeMode = BakeMode.ReplaceOriginal;
    private bool _stripRootMotion = true;
    private bool _forceGenericRig = true;
    private bool _removeKeyframeEvents = true;
    private bool _autoReplaceInControllers = true;
    private bool _disableFBXAnimationImport = true;
    private string _suffix = "_Baked";
    private List<BackupRecord> _backupHistory = new List<BackupRecord>();
    private bool _showBackups = true;

    [MenuItem("Tools/Luna Playable/Animation Bake Tool")]
    public static void ShowWindow()
    {
        LunaAnimationBakeTool window = GetWindow<LunaAnimationBakeTool>("Luna Animation Bake Tool");
        window.minSize = new Vector2(400, 420);
        window.Show();
    }

    private void OnGUI()
    {
        DrawHeader();

        EditorGUILayout.Space(10);
        
        // 1. Target Assets Selector (List with Drag & Drop)
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("Danh Sách FBX / .anim / Animator Controller Muốn Xử Lý", EditorStyles.boldLabel);
        
        // Drag and Drop Area
        Rect dropArea = GUILayoutUtility.GetRect(0.0f, 50.0f, GUILayout.ExpandWidth(true));
        GUI.Box(dropArea, "\nKéo và thả các file .fbx, .anim hoặc .controller vào đây", EditorStyles.helpBox);
        HandleDragAndDrop(dropArea);

        if (_targetAssets.Count > 0)
        {
            EditorGUILayout.Space(5);
            for (int i = 0; i < _targetAssets.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                _targetAssets[i] = EditorGUILayout.ObjectField($"Asset {i + 1}", _targetAssets[i], typeof(Object), true);
                if (GUILayout.Button("Xóa", GUILayout.Width(55)))
                {
                    _targetAssets.RemoveAt(i);
                    i--;
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Thêm ô trống"))
            {
                _targetAssets.Add(null);
            }
            if (GUILayout.Button("Xóa tất cả"))
            {
                _targetAssets.Clear();
            }
            EditorGUILayout.EndHorizontal();
        }
        else
        {
            EditorGUILayout.HelpBox("Kéo thả tệp tin hoặc nhấp nút 'Thêm ô trống' bên dưới để bắt đầu cấu hình.", MessageType.Info);
            if (GUILayout.Button("Thêm ô trống"))
            {
                _targetAssets.Add(null);
            }
        }
        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(10);

        // 2. Settings Group
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("Tùy Chọn Chế Độ Bake", EditorStyles.boldLabel);
        
        _bakeMode = (BakeMode)EditorGUILayout.EnumPopup("Chế độ Bake", _bakeMode);
        
        if (_bakeMode == BakeMode.CreateBakedClone)
        {
            _suffix = EditorGUILayout.TextField("Hậu tố tệp clone", _suffix);
        }

        _stripRootMotion = EditorGUILayout.Toggle("Loại bỏ Root Motion", _stripRootMotion);
        _forceGenericRig = EditorGUILayout.Toggle("Ép Rig thành Generic (FBX)", _forceGenericRig);
        _removeKeyframeEvents = EditorGUILayout.Toggle("Loại bỏ Keyframe Events", _removeKeyframeEvents);
        
        if (_bakeMode == BakeMode.ReplaceOriginal)
        {
            _autoReplaceInControllers = EditorGUILayout.Toggle("Tự động đổi trong Animator", _autoReplaceInControllers);
            _disableFBXAnimationImport = EditorGUILayout.Toggle("Tắt Anim trên FBX gốc", _disableFBXAnimationImport);
        }
        
        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(15);

        // 3. Action Button
        Color defaultColor = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.2f, 0.7f, 0.3f);
        if (GUILayout.Button("TIẾN HÀNH XỬ LÝ VÀ BAKE", GUILayout.Height(40)))
        {
            ExecuteBakeProcess();
        }
        GUI.backgroundColor = defaultColor;

        // 4. Backup History Group
        DrawBackupHistoryUI();
    }

    private void DrawHeader()
    {
        // Premium Title Design
        GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel);
        titleStyle.fontSize = 18;
        titleStyle.alignment = TextAnchor.MiddleCenter;
        titleStyle.normal.textColor = new Color(0.1f, 0.6f, 0.9f);

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("LUNA ANIMATION BAKE TOOL", titleStyle);
        EditorGUILayout.LabelField("Tối ưu hóa Rig & Hoạt ảnh chuyên dùng cho Luna WASM", EditorStyles.centeredGreyMiniLabel);
        EditorGUILayout.Space(5);
        
        // Horizontal divider
        Rect rect = EditorGUILayout.GetControlRect(false, 1);
        EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.3f));
    }

    private void HandleDragAndDrop(Rect dropArea)
    {
        Event currentEvent = Event.current;
        if (!dropArea.Contains(currentEvent.mousePosition)) return;

        if (currentEvent.type == EventType.DragUpdated || currentEvent.type == EventType.DragPerform)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

            if (currentEvent.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (Object draggedObject in DragAndDrop.objectReferences)
                {
                    string path = AssetDatabase.GetAssetPath(draggedObject);
                    bool isFBX = path.ToLower().EndsWith(".fbx");
                    bool isAnim = path.ToLower().EndsWith(".anim");
                    bool isController = path.ToLower().EndsWith(".controller") || draggedObject is AnimatorController;
                    if (isFBX || isAnim || isController)
                    {
                        if (!_targetAssets.Contains(draggedObject))
                        {
                            _targetAssets.Add(draggedObject);
                        }
                    }
                }
            }
            currentEvent.Use();
        }
    }

    private void ExecuteBakeProcess()
    {
        if (_targetAssets == null || _targetAssets.Count == 0)
        {
            EditorUtility.DisplayDialog("Cảnh Báo", "Vui lòng chọn ít nhất một FBX, .anim hoặc Animator Controller để thực hiện.", "OK");
            return;
        }

        HashSet<string> assetsToProcess = new HashSet<string>();
        
        for (int i = 0; i < _targetAssets.Count; i++)
        {
            Object target = _targetAssets[i];
            if (target == null) continue;

            string assetPath = AssetDatabase.GetAssetPath(target);
            bool isFBX = assetPath.ToLower().EndsWith(".fbx");
            bool isAnim = assetPath.ToLower().EndsWith(".anim");
            bool isController = assetPath.ToLower().EndsWith(".controller") || target is AnimatorController;

            if (isFBX)
            {
                assetsToProcess.Add(assetPath);
            }
            else if (isAnim)
            {
                assetsToProcess.Add(assetPath);
            }
            else if (isController)
            {
                AnimatorController controller = target as AnimatorController;
                if (controller != null)
                {
                    List<AnimationClip> clipsFromController = GetClipsFromController(controller);
                    foreach (AnimationClip clip in clipsFromController)
                    {
                        if (clip != null)
                        {
                            string clipPath = AssetDatabase.GetAssetPath(clip);
                            if (!string.IsNullOrEmpty(clipPath))
                            {
                                bool isClipFBX = clipPath.ToLower().EndsWith(".fbx");
                                bool isClipAnim = clipPath.ToLower().EndsWith(".anim");
                                if (isClipFBX || isClipAnim)
                                {
                                    assetsToProcess.Add(clipPath);
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                Debug.LogWarning($"[LunaBakeTool] Bỏ qua file không hợp lệ: {assetPath}");
            }
        }

        int processedCount = 0;
        foreach (string assetPath in assetsToProcess)
        {
            bool isFBX = assetPath.ToLower().EndsWith(".fbx");
            bool isAnim = assetPath.ToLower().EndsWith(".anim");

            // Case A & B: FBX asset or nested Animation Clip inside FBX
            if (isFBX)
            {
                ProcessFBXAdvanced(assetPath);
                processedCount++;
            }
            // Case C: Standalone .anim asset
            else if (isAnim)
            {
                if (_bakeMode == BakeMode.ReplaceOriginal)
                {
                    ProcessAnimationClipReplace(assetPath);
                }
                else
                {
                    ProcessAnimationClipClone(assetPath);
                }
                processedCount++;
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("Thành Công", $"Đã xử lý và bake hoàn tất cho {processedCount} tệp tin!", "OK");
    }

    private List<AnimationClip> GetClipsFromController(AnimatorController controller)
    {
        List<AnimationClip> clips = new List<AnimationClip>();
        if (controller == null) return clips;

        foreach (var layer in controller.layers)
        {
            GetClipsFromStateMachine(layer.stateMachine, clips);
        }
        return clips;
    }

    private void GetClipsFromStateMachine(AnimatorStateMachine stateMachine, List<AnimationClip> clips)
    {
        if (stateMachine == null) return;

        foreach (var childState in stateMachine.states)
        {
            var state = childState.state;
            if (state == null) continue;

            GetClipsFromMotion(state.motion, clips);
        }

        foreach (var subStateMachine in stateMachine.stateMachines)
        {
            GetClipsFromStateMachine(subStateMachine.stateMachine, clips);
        }
    }

    private void GetClipsFromMotion(Motion motion, List<AnimationClip> clips)
    {
        if (motion == null) return;

        if (motion is AnimationClip clip)
        {
            if (!clips.Contains(clip))
            {
                clips.Add(clip);
            }
        }
        else if (motion is BlendTree blendTree)
        {
            foreach (var child in blendTree.children)
            {
                GetClipsFromMotion(child.motion, clips);
            }
        }
    }

    private void ProcessFBXAdvanced(string fbxPath)
    {
        // 1. Load all nested AnimationClips inside the FBX
        Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
        List<AnimationClip> nestedClips = new List<AnimationClip>();
        foreach (Object subAsset in subAssets)
        {
            if (subAsset is AnimationClip clip && !clip.name.StartsWith("__preview__"))
            {
                nestedClips.Add(clip);
            }
        }

        if (nestedClips.Count == 0)
        {
            Debug.LogWarning($"[LunaBakeTool] Không tìm thấy hoạt ảnh nhúng nào trong FBX: {fbxPath}");
            return;
        }

        string directory = Path.GetDirectoryName(fbxPath);
        string fbxName = Path.GetFileNameWithoutExtension(fbxPath);

        // For ReplaceOriginal, backup the original FBX file
        if (_bakeMode == BakeMode.ReplaceOriginal)
        {
            CreateBackup(fbxPath);
        }

        Dictionary<string, AnimationClip> replacementClips = new Dictionary<string, AnimationClip>();

        foreach (AnimationClip nestedClip in nestedClips)
        {
            // Standalone .anim path:
            string cleanClipName = nestedClip.name.Replace("TempJoints|", "").Replace("|", "_");
            string newPath = Path.Combine(directory, $"{fbxName}_{cleanClipName}{_suffix}.anim").Replace("\\", "/");
            
            // Create standalone .anim file
            AnimationClip standaloneClip = Instantiate(nestedClip);
            standaloneClip.name = $"{fbxName}_{cleanClipName}{_suffix}";

            // Physically strip root motion curves from standalone .anim file (Fully writeable)
            if (_stripRootMotion)
            {
                StripRootMotionFromClip(standaloneClip);
            }

            if (_removeKeyframeEvents)
            {
                RemoveEventsFromClip(standaloneClip);
            }

            // Save standalone asset
            AssetDatabase.CreateAsset(standaloneClip, newPath);
            replacementClips[nestedClip.name] = AssetDatabase.LoadAssetAtPath<AnimationClip>(newPath);
            Debug.Log($"[LunaBakeTool] Đã trích xuất & bake hoạt ảnh độc lập thành công tại: {newPath}");
        }

        // Apply Rig transformation as Generic if requested
        if (_forceGenericRig)
        {
            ModelImporter importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer != null && importer.animationType != ModelImporterAnimationType.Generic)
            {
                Undo.RegisterCompleteObjectUndo(importer, "Change Rig to Generic");
                importer.animationType = ModelImporterAnimationType.Generic;
                EditorUtility.SetDirty(importer);
                AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);
                Debug.Log($"[LunaBakeTool] Đã chuyển đổi Rig của FBX '{fbxPath}' sang Generic.");
            }
        }

        if (_bakeMode == BakeMode.ReplaceOriginal)
        {
            // 2. Automatically replace references in Animator Controllers
            if (_autoReplaceInControllers)
            {
                AutoReplaceAnimationClipsInControllers(fbxPath, replacementClips);
            }

            // 3. Disable Animation Import on the original FBX to completely prevent Luna build crash
            if (_disableFBXAnimationImport)
            {
                ModelImporter importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
                if (importer != null && importer.importAnimation)
                {
                    Undo.RegisterCompleteObjectUndo(importer, "Disable FBX Animation Import");
                    importer.importAnimation = false; // Disable animation import completely!
                    EditorUtility.SetDirty(importer);
                    AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);
                    Debug.Log($"[LunaBakeTool] Đã tắt Import Animation trên FBX gốc '{fbxPath}' để tránh lỗi Luna.");
                }
            }
        }
    }

    private void AutoReplaceAnimationClipsInControllers(string fbxPath, Dictionary<string, AnimationClip> replacementClips)
    {
        string[] guids = AssetDatabase.FindAssets("t:AnimatorController");
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null) continue;

            bool controllerChanged = false;
            Undo.RegisterCompleteObjectUndo(controller, "Auto Replace Baked Animations");

            foreach (var layer in controller.layers)
            {
                if (ReplaceClipsInStateMachine(layer.stateMachine, fbxPath, replacementClips))
                {
                    controllerChanged = true;
                }
            }

            if (controllerChanged)
            {
                EditorUtility.SetDirty(controller);
                Debug.Log($"[LunaBakeTool] Đã tự động cập nhật Animator Controller: {path}");
            }
        }
    }

    private bool ReplaceClipsInStateMachine(AnimatorStateMachine stateMachine, string fbxPath, Dictionary<string, AnimationClip> replacementClips)
    {
        if (stateMachine == null) return false;
        bool changed = false;

        foreach (var childState in stateMachine.states)
        {
            var state = childState.state;
            if (state == null) continue;

            if (state.motion is AnimationClip clip)
            {
                string clipPath = AssetDatabase.GetAssetPath(clip);
                if (clipPath == fbxPath)
                {
                    if (replacementClips.TryGetValue(clip.name, out AnimationClip standaloneClip))
                    {
                        state.motion = standaloneClip;
                        changed = true;
                        Debug.Log($"[LunaBakeTool] State '{state.name}' đã được chuyển sang tệp độc lập '{standaloneClip.name}'.");
                    }
                }
            }
            else if (state.motion is BlendTree blendTree)
            {
                if (ReplaceClipsInBlendTree(blendTree, fbxPath, replacementClips))
                {
                    changed = true;
                }
            }
        }

        foreach (var subStateMachine in stateMachine.stateMachines)
        {
            if (ReplaceClipsInStateMachine(subStateMachine.stateMachine, fbxPath, replacementClips))
            {
                changed = true;
            }
        }

        return changed;
    }

    private bool ReplaceClipsInBlendTree(BlendTree blendTree, string fbxPath, Dictionary<string, AnimationClip> replacementClips)
    {
        if (blendTree == null) return false;
        bool changed = false;

        var children = blendTree.children;
        for (int i = 0; i < children.Length; i++)
        {
            var child = children[i];
            if (child.motion is AnimationClip clip)
            {
                string clipPath = AssetDatabase.GetAssetPath(clip);
                if (clipPath == fbxPath)
                {
                    if (replacementClips.TryGetValue(clip.name, out AnimationClip standaloneClip))
                    {
                        child.motion = standaloneClip;
                        changed = true;
                        Debug.Log($"[LunaBakeTool] BlendTree '{blendTree.name}' child {i} đã được chuyển sang tệp độc lập '{standaloneClip.name}'.");
                    }
                }
            }
            else if (child.motion is BlendTree subTree)
            {
                if (ReplaceClipsInBlendTree(subTree, fbxPath, replacementClips))
                {
                    changed = true;
                }
            }
        }

        if (changed)
        {
            blendTree.children = children;
            EditorUtility.SetDirty(blendTree);
        }

        return changed;
    }

    private void ProcessAnimationClipReplace(string path)
    {
        AnimationClip sourceClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if (sourceClip == null)
        {
            Debug.LogError($"[LunaBakeTool] Không thể tải AnimationClip tại: {path}");
            return;
        }

        CreateBackup(path);
        Undo.RegisterCompleteObjectUndo(sourceClip, "Bake Animation In-Place");

        if (_stripRootMotion)
        {
            StripRootMotionFromClip(sourceClip);
        }

        if (_removeKeyframeEvents)
        {
            RemoveEventsFromClip(sourceClip);
        }

        EditorUtility.SetDirty(sourceClip);
    }

    private void ProcessAnimationClipClone(string path)
    {
        AnimationClip sourceClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if (sourceClip == null)
        {
            Debug.LogError($"[LunaBakeTool] Không thể tải AnimationClip tại: {path}");
            return;
        }

        string directory = Path.GetDirectoryName(path);
        string filename = Path.GetFileNameWithoutExtension(path);
        string newPath = Path.Combine(directory, $"{filename}{_suffix}.anim").Replace("\\", "/");

        AnimationClip newClip = Instantiate(sourceClip);
        newClip.name = $"{sourceClip.name}{_suffix}";

        if (_stripRootMotion)
        {
            StripRootMotionFromClip(newClip);
        }

        if (_removeKeyframeEvents)
        {
            RemoveEventsFromClip(newClip);
        }

        AssetDatabase.CreateAsset(newClip, newPath);
        Debug.Log($"[LunaBakeTool] Đã tạo hoạt ảnh clone thành công tại: {newPath}");
    }

    private void StripRootMotionFromClip(AnimationClip clip)
    {
        var bindings = AnimationUtility.GetCurveBindings(clip);
        int strippedCount = 0;
        
        for (int i = 0; i < bindings.Length; i++)
        {
            var binding = bindings[i];
            if (string.IsNullOrEmpty(binding.path))
            {
                AnimationUtility.SetEditorCurve(clip, binding, null);
                strippedCount++;
            }
        }

        var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
        for (int i = 0; i < objectBindings.Length; i++)
        {
            var objBinding = objectBindings[i];
            if (string.IsNullOrEmpty(objBinding.path))
            {
                AnimationUtility.SetObjectReferenceCurve(clip, objBinding, null);
                strippedCount++;
            }
        }

        Debug.Log($"[LunaBakeTool] Đã lọc bỏ {strippedCount} Root Motion curves khỏi hoạt ảnh '{clip.name}'.");
    }

    private void RemoveEventsFromClip(AnimationClip clip)
    {
        var events = AnimationUtility.GetAnimationEvents(clip);
        if (events != null && events.Length > 0)
        {
            AnimationUtility.SetAnimationEvents(clip, new AnimationEvent[0]);
            Debug.Log($"[LunaBakeTool] Đã loại bỏ {events.Length} Keyframe Events khỏi hoạt ảnh '{clip.name}'.");
        }
    }

    private string GetBackupDirectory()
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "ProjectSettings", "LunaBakeBackups");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    private void CreateBackup(string assetPath)
    {
        string absoluteAssetPath = Path.Combine(Directory.GetCurrentDirectory(), assetPath).Replace("\\", "/");
        if (!File.Exists(absoluteAssetPath)) return;

        string backupDir = GetBackupDirectory();
        string guid = AssetDatabase.AssetPathToGUID(assetPath);
        if (string.IsNullOrEmpty(guid)) guid = System.Guid.NewGuid().ToString();

        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string filename = Path.GetFileName(assetPath);
        string backupFilename = $"{guid}_{timestamp}_{filename}";
        string backupPath = Path.Combine(backupDir, backupFilename);

        File.Copy(absoluteAssetPath, backupPath, true);

        BackupRecord record = new BackupRecord
        {
            originalAssetPath = assetPath,
            backupTempPath = backupPath,
            timestamp = System.DateTime.Now
        };
        
        _backupHistory.Insert(0, record);
    }

    private void RestoreBackup(BackupRecord record)
    {
        if (record == null) return;
        if (!File.Exists(record.backupTempPath))
        {
            Debug.LogError($"[LunaBakeTool] File backup không tồn tại tại: {record.backupTempPath}");
            return;
        }

        string destPath = Path.Combine(Directory.GetCurrentDirectory(), record.originalAssetPath);
        
        // Connect to Undo system
        Object asset = AssetDatabase.LoadAssetAtPath<Object>(record.originalAssetPath);
        if (asset != null)
        {
            Undo.RegisterCompleteObjectUndo(asset, "Restore Animation Backup");
        }

        File.Copy(record.backupTempPath, destPath, true);
        AssetDatabase.ImportAsset(record.originalAssetPath, ImportAssetOptions.ForceUpdate);
        AssetDatabase.Refresh();

        Debug.Log($"[LunaBakeTool] Đã khôi phục thành công {record.originalAssetPath} từ bản backup.");
    }

    private void DrawBackupHistoryUI()
    {
        if (_backupHistory == null || _backupHistory.Count == 0) return;

        EditorGUILayout.Space(10);
        _showBackups = EditorGUILayout.BeginFoldoutHeaderGroup(_showBackups, $"Lịch Sử Bản Sao Lưu Dự Phòng ({_backupHistory.Count})");
        if (_showBackups)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            for (int i = 0; i < _backupHistory.Count; i++)
            {
                var record = _backupHistory[i];
                if (record == null) continue;

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"{Path.GetFileName(record.originalAssetPath)} ({record.timestamp:HH:mm:ss})", EditorStyles.miniLabel);
                
                if (GUILayout.Button("Khôi Phục", GUILayout.Width(80)))
                {
                    if (EditorUtility.DisplayDialog("Khôi Phục", $"Bạn có chắc muốn khôi phục tệp {Path.GetFileName(record.originalAssetPath)} về trạng thái lúc {record.timestamp:yyyy-MM-dd HH:mm:ss}?", "Có, Khôi phục", "Không"))
                    {
                        RestoreBackup(record);
                        _backupHistory.RemoveAt(i);
                        i--;
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }
        EditorGUILayout.EndFoldoutHeaderGroup();
    }
}
#endif
