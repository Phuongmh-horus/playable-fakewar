using UnityEditor;
using UnityEngine;

namespace OptimizedFeature.Scripts.Editor
{
    [CustomEditor(typeof(VATTestingController))]
    public class VATTestingControllerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            // Draw default properties
            DrawDefaultInspector();

            VATTestingController tester = (VATTestingController)target;
            if (tester == null) return;

            VAT_RenderComponent vat = tester.VatComponent;
            if (vat == null)
            {
                EditorGUILayout.HelpBox("Please assign a VAT_RenderComponent reference.", MessageType.Warning);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime Monitor", EditorStyles.boldLabel);
            
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField("Current State", vat.CurrentStateName);
            EditorGUILayout.IntField("Current State Hash", vat.CurrentStateHash);
            EditorGUILayout.Toggle("Is Blending", vat.IsBlending);
            EditorGUI.EndDisabledGroup();

            if (vat.VatAssetData == null)
            {
                EditorGUILayout.HelpBox("The VAT_RenderComponent has no VATAssetDataSO assigned.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Animation Control Deck", EditorStyles.boldLabel);

            if (GUILayout.Button("Populate States from Asset"))
            {
                tester.AutoPopulateClips();
                serializedObject.Update();
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to interactively trigger animation changes.", MessageType.Info);
                return;
            }

            var clips = vat.VatAssetData.Clips;
            if (clips == null || clips.Count == 0)
            {
                EditorGUILayout.HelpBox("No animations found in VATAssetData.", MessageType.Warning);
                return;
            }

            EditorGUILayout.Space();
            for (int i = 0; i < clips.Count; i++)
            {
                VATClipInfo clip = clips[i];
                if (clip == null) continue;

                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField($"State: <b>{clip.ClipName}</b> (Hash: {clip.StateHash})", new GUIStyle(EditorStyles.label) { richText = true });
                
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Play (String)"))
                {
                    tester.PlayByName(clip.ClipName);
                }
                if (GUILayout.Button("Play (Hash)"))
                {
                    tester.PlayByHash(clip.ClipName);
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("CrossFade (String)"))
                {
                    tester.CrossFadeByName(clip.ClipName);
                }
                if (GUILayout.Button("CrossFade (Hash)"))
                {
                    tester.CrossFadeByHash(clip.ClipName);
                }
                EditorGUILayout.EndHorizontal();
                
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }
        }
    }
}
