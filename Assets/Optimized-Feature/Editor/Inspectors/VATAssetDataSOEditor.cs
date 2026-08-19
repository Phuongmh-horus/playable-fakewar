using UnityEditor;
using UnityEngine;
using OptimizedFeature.Editor.VATAnimator;

namespace OptimizedFeature.Scripts.Editor
{
    /// <summary>
    /// Keeps VATAssetDataSO as the entry point for animation authoring. The
    /// animator payload itself stays in the linked VATAssetAnimatorSO.
    /// </summary>
    [CustomEditor(typeof(VATAssetDataSO))]
    public sealed class VATAssetDataSOEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            VATAssetDataSO assetData = target as VATAssetDataSO;
            if (assetData == null)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("VAT Animator", EditorStyles.boldLabel);

            bool needsAnimatorAsset = assetData.AnimatorAsset == null;
            EditorGUILayout.HelpBox(
                needsAnimatorAsset
                    ? "No VATAssetAnimatorSO is linked. Open Animator will create one beside this VATAssetDataSO, then open its graph."
                    : "Open Animator edits the graph stored in the linked VATAssetAnimatorSO.",
                needsAnimatorAsset ? MessageType.Info : MessageType.None);

            if (GUILayout.Button("Open Animator", GUILayout.Height(24f)))
            {
                VATAnimatorGraphWindow.OpenForAsset(assetData, needsAnimatorAsset);
            }

            EditorGUILayout.EndVertical();
        }
    }
}
