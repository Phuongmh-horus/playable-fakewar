using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace OptimizedFeature.Scripts.Editor
{
    /// <summary>
    /// Editor Window utility to automate the runtime setup of baked VAT characters.
    /// Accepts a list of target GameObjects for batch setup.
    /// </summary>
    public class VATSetupHelper : EditorWindow
    {
        [SerializeField] private List<GameObject> _targetRoots = new List<GameObject>();
        private VATAssetDataSO _vatAssetData;
        private Material _vatMaterial;

        [System.Serializable]
        public class SocketAttachmentSetup
        {
            public GameObject EquipmentObject;
            public string SocketName = "RightHand";
        }

        [SerializeField]
        private List<SocketAttachmentSetup> _attachments = new List<SocketAttachmentSetup>();

        private SerializedObject _serializedObject;
        private SerializedProperty _attachmentsProperty;
        private SerializedProperty _targetRootsProperty;
        private Vector2 _scrollPosition;

        [MenuItem("Tools/VAT Setup Tester Helper")]
        public static void OpenWindow()
        {
            GetWindow<VATSetupHelper>("VAT Setup Tester");
        }

        private void OnEnable()
        {
            _serializedObject = new SerializedObject(this);
            _attachmentsProperty = _serializedObject.FindProperty("_attachments");
            _targetRootsProperty = _serializedObject.FindProperty("_targetRoots");
        }

        private void OnGUI()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            EditorGUILayout.LabelField("VAT Runtime Setup Helper", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Drag & drop multiple GameObjects to batch setup VAT components.\n" +
                "• Adds VAT_RenderComponent + MeshFilter + MeshRenderer on each root\n" +
                "• Cleans up legacy child 'MeshRenderer_VAT' objects if found",
                MessageType.Info);

            EditorGUILayout.Space();

            // --- Shared Settings ---
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Shared Settings", EditorStyles.boldLabel);
            _vatAssetData = (VATAssetDataSO)EditorGUILayout.ObjectField("VAT Asset Data SO", _vatAssetData, typeof(VATAssetDataSO), false);
            _vatMaterial = (Material)EditorGUILayout.ObjectField("VAT Material (Optional)", _vatMaterial, typeof(Material), false);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space();

            // --- Target List ---
            _serializedObject.Update();
            EditorGUILayout.PropertyField(_targetRootsProperty, new GUIContent("Target GameObjects"), true);

            EditorGUILayout.Space();

            // --- Quick Add from Selection ---
            if (GUILayout.Button("+ Add Selected Objects from Hierarchy"))
            {
                AddSelectedObjects();
            }

            EditorGUILayout.Space();

            // --- Equipment Attachments ---
            EditorGUILayout.PropertyField(_attachmentsProperty, new GUIContent("Equipment Attachments (Applied to all)"), true);
            _serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();

            // --- Validation ---
            int validCount = CountValidTargets();
            bool canSetup = validCount > 0 && _vatAssetData != null &&
                            (_vatMaterial != null || (_vatAssetData.BakedMaterials != null && _vatAssetData.BakedMaterials.Count > 0));

            EditorGUILayout.LabelField($"Ready: {validCount} object(s)", EditorStyles.miniLabel);

            // --- Setup Button ---
            EditorGUI.BeginDisabledGroup(!canSetup);
            if (GUILayout.Button($"Setup {validCount} VAT Character(s)", GUILayout.Height(30)))
            {
                SetupAllVATCharacters();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndScrollView();
        }

        private void AddSelectedObjects()
        {
            GameObject[] selected = Selection.gameObjects;
            if (selected == null || selected.Length == 0)
            {
                Debug.LogWarning("[VATSetupHelper] No objects selected in Hierarchy.");
                return;
            }

            int added = 0;
            foreach (var go in selected)
            {
                if (go != null && !_targetRoots.Contains(go))
                {
                    _targetRoots.Add(go);
                    added++;
                }
            }
            Debug.Log($"[VATSetupHelper] Added {added} object(s) from selection.");
        }

        private int CountValidTargets()
        {
            int count = 0;
            for (int i = 0; i < _targetRoots.Count; i++)
            {
                if (_targetRoots[i] != null) count++;
            }
            return count;
        }

        private void SetupAllVATCharacters()
        {
            if (_vatAssetData == null) return;

            List<Material> targetMaterials = new List<Material>();
            if (_vatMaterial != null)
            {
                targetMaterials.Add(_vatMaterial);
            }
            else if (_vatAssetData.BakedMaterials != null && _vatAssetData.BakedMaterials.Count > 0)
            {
                targetMaterials.AddRange(_vatAssetData.BakedMaterials);
            }

            if (targetMaterials.Count == 0)
            {
                Debug.LogError("[VATSetupHelper] No materials specified and no baked materials found in VATAssetDataSO!");
                return;
            }

            // Ensure VATSystem exists in the scene
            VATSystem system = FindObjectOfType<VATSystem>();
            if (system == null)
            {
                GameObject systemGo = new GameObject("VATSystem");
                system = systemGo.AddComponent<VATSystem>();
                Debug.Log("[VATSetupHelper] Created VATSystem Manager in the scene.");
            }

            int successCount = 0;
            for (int i = 0; i < _targetRoots.Count; i++)
            {
                GameObject target = _targetRoots[i];
                if (target == null) continue;

                SetupSingleCharacter(target, targetMaterials);
                successCount++;
            }

            Debug.Log($"[VATSetupHelper] Successfully configured {successCount} VAT character(s)!");
        }

        private void SetupSingleCharacter(GameObject target, List<Material> targetMaterials)
        {
            Undo.RegisterFullObjectHierarchyUndo(target, "Setup VAT Character");

            // --- Clean up legacy child 'MeshRenderer_VAT' if it exists ---
            Transform legacyChild = target.transform.Find("MeshRenderer_VAT");
            if (legacyChild != null)
            {
                Debug.Log($"[VATSetupHelper] Removing legacy child 'MeshRenderer_VAT' from {target.name}");
                Undo.DestroyObjectImmediate(legacyChild.gameObject);
            }

            // 1. Add/Get VAT_RenderComponent on the root (includes MeshFilter + MeshRenderer via RequireComponent)
            VAT_RenderComponent renderComponent = target.GetComponent<VAT_RenderComponent>();
            if (renderComponent == null)
            {
                renderComponent = Undo.AddComponent<VAT_RenderComponent>(target);
            }

            // 2. Configure VAT data and materials directly on the unified component
            MeshFilter meshFilter = target.GetComponent<MeshFilter>();
            MeshRenderer meshRenderer = target.GetComponent<MeshRenderer>();

            // Set references via SerializedObject to guarantee persistence in Edit Mode
            var serializedComponent = new SerializedObject(renderComponent);
            serializedComponent.FindProperty("_meshFilter").objectReferenceValue = meshFilter;
            serializedComponent.FindProperty("_meshRenderer").objectReferenceValue = meshRenderer;
            serializedComponent.FindProperty("_vatAssetData").objectReferenceValue = _vatAssetData;
            serializedComponent.ApplyModifiedProperties();

            // Apply data and material configurations
            renderComponent.SetMaterials(targetMaterials.ToArray());
            renderComponent.SetVATAssetData(_vatAssetData);

            // 3. Configure Equipment Attachments
            foreach (var setup in _attachments)
            {
                if (setup.EquipmentObject == null) continue;

                setup.EquipmentObject.transform.SetParent(target.transform, false);

                VAT_ObjectMesh objectMeshBridge = setup.EquipmentObject.GetComponent<VAT_ObjectMesh>();
                if (objectMeshBridge == null)
                {
                    objectMeshBridge = setup.EquipmentObject.AddComponent<VAT_ObjectMesh>();
                }

                var serializedObjMesh = new SerializedObject(objectMeshBridge);
                serializedObjMesh.FindProperty("_socketName").stringValue = setup.SocketName;
                serializedObjMesh.FindProperty("_animatorBridge").objectReferenceValue = renderComponent;
                serializedObjMesh.ApplyModifiedProperties();

                objectMeshBridge.BindSocketData();
            }

            // 4. Register with VATSystem
            VATSystem.RegisterAnimator(renderComponent);

            EditorUtility.SetDirty(target);

            // Apply to prefab if applicable
            if (PrefabUtility.IsPartOfPrefabInstance(target))
            {
                PrefabUtility.ApplyPrefabInstance(target, InteractionMode.AutomatedAction);
                Debug.Log($"[VATSetupHelper] Applied prefab modifications: {target.name}");
            }

            Debug.Log($"[VATSetupHelper] Configured: {target.name}");
        }
    }
}
