using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AnimationInstancing
{
    [CustomEditor(typeof(AnimationInstancing))]
    public class AnimationInstancingInspector : Editor
    {
        #region Inspector GUI

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            AnimationInstancing instance = (AnimationInstancing)target;
            TextAsset animationData = instance.animationData;
            if (animationData == null)
            {
                string prefabName = instance.prototype != null ? instance.prototype.name : instance.gameObject.name;
                animationData = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/AnimationTexture/" + prefabName + ".bytes");
            }

            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Baked Animation Info", EditorStyles.boldLabel);

            if (animationData == null)
            {
                EditorGUILayout.HelpBox("Không tìm thấy file animation data đã bake.", MessageType.Info);
            }
            else
            {
                List<AnimationInfo> animationInfos = ReadAnimationInfo(animationData.bytes);
                if (animationInfos == null)
                {
                    EditorGUILayout.HelpBox("Không thể đọc animation data đã bake.", MessageType.Warning);
                }
                else
                {
                    for (int i = 0; i < animationInfos.Count; ++i)
                    {
                        EditorGUILayout.LabelField(string.Format("{0} - {1} (Baked frame: {2})",
                            i,
                            animationInfos[i].animationName,
                            animationInfos[i].animationIndex));
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        #endregion

        #region Support Methods

        private List<AnimationInfo> ReadAnimationInfo(byte[] data)
        {
            try
            {
                using (BinaryReader reader = new BinaryReader(new MemoryStream(data)))
                {
                    int count = reader.ReadInt32();
                    List<AnimationInfo> animationInfos = new List<AnimationInfo>(count);
                    for (int i = 0; i < count; ++i)
                    {
                        AnimationInfo info = new AnimationInfo();
                        info.animationName = reader.ReadString();
                        info.animationNameHash = info.animationName.GetHashCode();
                        info.animationIndex = reader.ReadInt32();
                        reader.ReadInt32();
                        info.totalFrame = reader.ReadInt32();
                        reader.ReadInt32();
                        bool rootMotion = reader.ReadBoolean();
                        reader.ReadInt32();

                        if (rootMotion)
                        {
                            reader.BaseStream.Position += info.totalFrame * 24;
                        }

                        int eventCount = reader.ReadInt32();
                        for (int j = 0; j < eventCount; ++j)
                        {
                            reader.ReadString();
                            reader.ReadSingle();
                            reader.ReadInt32();
                            reader.ReadString();
                            reader.ReadSingle();
                            reader.ReadString();
                        }

                        animationInfos.Add(info);
                    }

                    return animationInfos;
                }
            }
            catch (EndOfStreamException)
            {
                return null;
            }
        }

        #endregion
    }
}
